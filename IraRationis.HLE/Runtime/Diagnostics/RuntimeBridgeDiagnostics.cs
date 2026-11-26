// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics.Models;
using IraRationis.Loader.Images;
using IraRationis.PE.Models;

namespace IraRationis.HLE.Runtime.Diagnostics;

public sealed class RuntimeBridgeDiagnostics : IHostFunctionObserver, IDisposable
{
    private static RuntimeBridgeDiagnostics? _active;
    private readonly RuntimeBridgeTrace _trace;
    private readonly RuntimeBridgeAbiPolicy _abi;
    private readonly Func<string> _captureCaller;
    private RuntimeBridgeImage[] _images = [];
    private int _disposed;
    private int _unverifiedRuntimeReported;

    private RuntimeBridgeDiagnostics(TextWriter writer, RuntimeBridgeOptions options)
    {
        _trace = new RuntimeBridgeTrace(writer, options.PerOperationLimit);
        _abi = RuntimeBridgeAbiPolicy.Capture();
        _captureCaller = () => Escape(RuntimeBridgeCallSite.Capture(Volatile.Read(ref _images)));
    }

    internal static RuntimeBridgeDiagnostics? Active
    {
        get
        {
            return Volatile.Read(ref _active);
        }
    }

    public static RuntimeBridgeDiagnostics? Begin(TextWriter writer, RuntimeBridgeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        options ??= RuntimeBridgeOptions.FromEnvironment();

        if (!options.Enabled)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Runtime bridge diagnostics require Windows x64.");
        }

        if (options.PerOperationLimit is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The diagnostic limit must be between 1 and 4096.");
        }

        var session = new RuntimeBridgeDiagnostics(writer, options);

        if (Interlocked.CompareExchange(ref _active, session, null) is not null)
        {
            throw new InvalidOperationException("A runtime bridge diagnostic session is already active.");
        }

        session._trace.Note(
            $"enabled=True; perOperationLimit={options.PerOperationLimit}; scope=title-import-and-observed-loader-calls; runtimeCalls=InitializeApiImpl,InitializeApiImplEx,InitializeApiImplEx2,QueryApiImpl; optionsContents=opaque; returnedInterfaces=unwrapped");

        session._trace.Note($"privateAbiVerified={session._abi.Verified}; runtimeFileSha256={session._abi.Fingerprint}; exportRvaChecks=True");

        return session;
    }

    public void RegisterImages(IEnumerable<LoadedImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);

        Volatile.Write(ref _images, images.Select(image => new RuntimeBridgeImage(
            Path.GetFileName(image.Path), image.BaseAddress, image.Info.SizeOfImage)).ToArray());
    }

    public void DescribeContext(string titlePath)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            _trace.Note(RuntimeBridgeContext.Describe(titlePath));
        }
        catch
        {
            _trace.Note("hostContext=unavailable");
        }
        finally
        {
            errorState.Restore();
        }
    }

    public nuint Observe(string library, ImportSymbol symbol, nuint address)
    {
        if (Active != this || address == 0 || symbol.Name is not { } name)
        {
            return address;
        }

        return (nuint)RuntimeBridgeCallbacks.ObserveHostFunction(library, name, (nint)address);
    }

    internal static RuntimeBridgeCall BeginLoad(nint name, bool unicode, uint flags)
    {
        var session = Active;

        if (session is null)
        {
            return default;
        }

        try
        {
            var readable = unicode
                ? RuntimeBridgeMemoryReader.TryReadUtf16(name, 512, out var requested)
                : RuntimeBridgeMemoryReader.TryReadAnsi(name, 512, out requested);

            return readable && IsRuntimeModule(requested)
                ? session.BeginCall(RuntimeBridgeOperation.LibraryLoad,
                    $"requested={Escape(requested)}; flags=0x{flags:X8}")
                : default;
        }
        catch
        {
            return default;
        }
    }

    internal static RuntimeBridgeCall BeginLookup(nint module, nint name)
    {
        var session = Active;

        if (session is null)
        {
            return default;
        }

        try
        {
            var path = RuntimeBridgeNativeMethods.GetModulePath(module);

            if (path is null || !IsRuntimeModule(path))
            {
                return default;
            }

            var requested = (nuint)name <= ushort.MaxValue
                ? $"ordinal:{(nuint)name}"
                : RuntimeBridgeMemoryReader.TryReadAnsi(name, 256, out var export) ? Escape(export) : "unreadable";

            return session.BeginCall(RuntimeBridgeOperation.ExportLookup,
                $"module=0x{module:X}; modulePath={Escape(path)}; export={requested}");
        }
        catch
        {
            return default;
        }
    }

    internal static RuntimeBridgeCall BeginInitialization(RuntimeBridgeOperation operation,
        ulong firstVersion, ulong secondVersion, ulong flags = 0, nint options = 0)
    {
        try
        {
            return Active?.BeginCall(operation,
                $"version1=0x{firstVersion:X16}; version2=0x{secondVersion:X16}; flags=0x{flags:X16}; options=0x{options:X}") ?? default;
        }
        catch
        {
            return default;
        }
    }

    internal static RuntimeBridgeCall BeginQuery(nint provider, nint requestedInterface, nint output)
    {
        try
        {
            var session = Active;

            if (session is null)
            {
                return default;
            }

            var providerReadable = RuntimeBridgeMemoryReader.TryReadGuid(provider, out var providerIdentifier);
            var interfaceReadable = RuntimeBridgeMemoryReader.TryReadGuid(requestedInterface, out var interfaceIdentifier);
            var key = new RuntimeBridgeQueryKey(providerIdentifier, interfaceIdentifier, providerReadable, interfaceReadable);
            var observation = session._trace.BeginQuery(key, output, session._captureCaller);

            return new RuntimeBridgeCall(session, RuntimeBridgeOperation.QueryApi, observation.Id, observation);
        }
        catch
        {
            return default;
        }
    }

    internal static void EndLoad(RuntimeBridgeCall call, nint result, int error)
    {
        try
        {
            if (call.Session is { } session)
            {
                var path = result == 0 ? null : RuntimeBridgeNativeMethods.GetModulePath(result);

                session._trace.End(call.Operation, call.Id,
                    $"module=0x{result:X}; succeeded={result != 0}; win32Error={error}; modulePath={Escape(path ?? "unavailable")}");
            }
        }
        catch
        {
        }
    }

    internal static nint EndLookup(RuntimeBridgeCall call, nint module, nint name, nint result, int error)
    {
        try
        {
            var returnedAddress = result;

            if (Active is { } session && result != 0 && (nuint)name > ushort.MaxValue &&
                RuntimeBridgeMemoryReader.TryReadAnsi(name, 256, out var export))
            {
                var path = RuntimeBridgeNativeMethods.GetModulePath(module);

                if (path is not null)
                {
                    if (!IsRuntimeModule(path))
                    {
                        returnedAddress = RuntimeBridgeCallbacks.ObserveHostFunction(Path.GetFileName(path), export, result);
                    }
                    else if (session._abi.CanObserve(path, module, export, result))
                    {
                        returnedAddress = RuntimeBridgeCallbacks.ObserveRuntimeExport(export, result);
                    }
                    else if (RuntimeBridgeAbiPolicy.ExportRva(export) != 0 &&
                             Interlocked.Exchange(ref session._unverifiedRuntimeReported, 1) == 0)
                    {
                        session._trace.Note("privateAbi=unverified; privateCalls=unwrapped; loaderAndExportObservation=True");
                    }
                }
            }

            call.Session?._trace.End(call.Operation, call.Id,
                $"resolved=0x{result:X}; returned=0x{returnedAddress:X}; wrapped={returnedAddress != result}; win32Error={error}");

            return returnedAddress;
        }
        catch
        {
            return result;
        }
    }

    internal static void EndResult(RuntimeBridgeCall call, int result, int error, nint output = 0)
    {
        try
        {
            if (call.Session is not { } session)
            {
                return;
            }

            if (call.Query is { } query)
            {
                session._trace.EndQuery(query, result, error, session._captureCaller);

                return;
            }

            var returnedObject = "notRead";

            if (call.Operation == RuntimeBridgeOperation.QueryApi && result >= 0)
            {
                returnedObject = RuntimeBridgeMemoryReader.TryReadPointer(output, out var value)
                    ? $"0x{value:X}" : "unreadable";
            }

            session._trace.End(call.Operation, call.Id,
                $"hresult=0x{unchecked((uint)result):X8}; succeeded={result >= 0}; win32Error={error}; returnedObject={returnedObject}");
        }
        catch
        {
        }
    }

    internal static void NoteBinding(string name, nint target, nint returned)
    {
        Active?._trace.Note($"binding={Escape(name)}; target=0x{target:X}; returned=0x{returned:X}; wrapped={target != returned}");
    }

    internal static bool IsRuntimeModule(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));

        return path.AsSpan(separator + 1).Equals("xgameruntime.dll", StringComparison.OrdinalIgnoreCase);
    }

    private RuntimeBridgeCall BeginCall(RuntimeBridgeOperation operation, string details)
    {
        var id = _trace.Begin(operation, details, _captureCaller);

        return new RuntimeBridgeCall(this, operation, id);
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\u003B", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.CompareExchange(ref _active, null, this);

                _trace.WriteSummary();
            }
        }
        finally
        {
            errorState.Restore();
        }
    }
}
