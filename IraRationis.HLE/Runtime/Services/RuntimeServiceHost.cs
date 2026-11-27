// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics;
using IraRationis.HLE.Runtime.Services.Async;
using IraRationis.HLE.Runtime.Services.Storage.PersistentLocalStorageService;
using IraRationis.HLE.Runtime.Services.Users.LocalUserService;
using IraRationis.HLE.Runtime.Services.Users.Models;

namespace IraRationis.HLE.Runtime.Services;

public sealed unsafe partial class RuntimeServiceHost
{
    private static RuntimeServiceHost? _active;
    private readonly RuntimeBridgeAbiPolicy _abi;
    private readonly Lock _bindingGate = new();
    private readonly Lazy<PersistentLocalStorageService> _storage;
    private readonly Lazy<TitleRuntimeService> _title;
    private readonly RuntimeServiceTrace _trace;
    private readonly Lazy<LocalUserService> _users;
    private nint _moduleReference;
    private nint _query;

    private RuntimeServiceHost(RuntimeBridgeAbiPolicy abi, TextWriter writer)
    {
        _abi = abi;
        _trace = new RuntimeServiceTrace(writer);
        _users = new Lazy<LocalUserService>(CreateUsers);
        _storage = new Lazy<PersistentLocalStorageService>(() => new PersistentLocalStorageService(
            Path.Combine(RuntimeState.CurrentTitle.Storage, "persistent"), _trace,
            RuntimeState.CurrentTitle.StorageBytes));
        _title = new Lazy<TitleRuntimeService>(() => new TitleRuntimeService(RuntimeState.CurrentTitle.Id, _trace));
    }

    internal static RuntimeServiceHost? Active
    {
        get
        {
            return Volatile.Read(ref _active);
        }
    }

    public static RuntimeServiceHost? CreateFromEnvironment(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (Environment.GetEnvironmentVariable("IRX_RUNTIME_LOCAL_SERVICES") != "1")
        {
            return null;
        }

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Local runtime services require Windows x64.");
        }

        var abi = RuntimeBridgeAbiPolicy.Capture();

        if (!abi.Verified)
        {
            throw new NotSupportedException(
                "The installed runtime ABI is not verified for local services. Disable IRX_RUNTIME_LOCAL_SERVICES to use the host runtime.");
        }

        var session = new RuntimeServiceHost(abi, writer);

        if (Interlocked.CompareExchange(ref _active, session, null) is not null)
        {
            throw new InvalidOperationException("Local runtime services already own this process.");
        }

        session._trace.Write("Enabled",
            "localUser=True; persistentStorage=True; onlineIdentity=False; hostServiceAllowlist=True");

        return session;
    }

    internal nint? ResolveExport(nint module, byte* name, nint original)
    {
        if (original == 0 || !RuntimeBridgeMemoryReader.TryReadAnsi((nint)name, 64, out var export)
                          || export != "QueryApiImpl")
        {
            return null;
        }

        var path = RuntimeBridgeNativeMethods.GetModulePath(module);

        if (path is null || !RuntimeBridgeDiagnostics.IsRuntimeModule(path))
        {
            return null;
        }

        if (!_abi.CanObserve(path, module, export, original))
        {
            _trace.Write("RuntimeBindingRejected", "reason=unverifiedPrivateAbi");

            return 0;
        }

        lock (_bindingGate)
        {
            if (_query != 0 && (_query != original || _moduleReference != module))
            {
                _trace.Write("RuntimeBindingRejected", "reason=conflictingRuntimeAddress");

                return 0;
            }

            if (_query == 0)
            {
                if (GetModuleHandleExW(4, (char*)original, out var ownedModule) == 0)
                {
                    return 0;
                }

                // The service objects and their host async backend remain valid until process exit.
                _moduleReference = ownedModule;
                _query = original;
            }
        }

        return (nint)(delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)&Query;
    }

    private LocalUserService CreateUsers()
    {
        var provider = RuntimeServiceRoutingPolicy.AsyncProvider;
        nint asyncObject = 0;
        var result = InvokeHost(&provider, &provider, &asyncObject);

        if (result < 0 || asyncObject == 0)
        {
            throw new InvalidOperationException(
                $"The runtime async service is unavailable (0x{unchecked((uint)result):X8}).");
        }

        // These identifiers belong only to the emulator's local profile, not an authenticated online account.
        var asyncService = new HostRuntimeAsyncService(asyncObject);

        try
        {
            return new LocalUserService(asyncService,
                new LocalUserProfile(1, 0x4952580000000001, "IraRationis"), _trace);
        }
        catch
        {
            asyncService.Dispose();

            throw;
        }
    }

    private int InvokeHost(Guid* provider, Guid* requested, nint* output)
    {
        return ((delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)_query)(provider, requested, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(Guid* provider, Guid* requested, nint* output)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginQuery((nint)provider, (nint)requested, (nint)output);

        incoming.Restore();

        var result = RuntimeServiceResult.Failure;

        try
        {
            if (provider == null || requested == null || output == null)
            {
                result = RuntimeServiceResult.InvalidArgument;
            }
            else
            {
                *output = 0;
                result = Active!.ResolveService(provider, requested, output);
            }
        }
        catch (Exception exception)
        {
            Active?._trace.Write("ServiceFailure", $"type={exception.GetType().Name}; hresult=0x80004005");
        }

        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndResult(call, result, outgoing.SystemError, (nint)output);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    private int ResolveService(Guid* provider, Guid* requested, nint* output)
    {
        if (*provider == LocalUserService.Provider)
        {
            return _users.Value.TryQuery(*requested, out *output)
                ? RuntimeServiceResult.Success
                : RuntimeServiceResult.NoInterface;
        }

        if (*provider == PersistentLocalStorageService.ProviderId)
        {
            return _storage.Value.TryQuery(*requested, out *output)
                ? RuntimeServiceResult.Success
                : RuntimeServiceResult.NoInterface;
        }

        if (*provider == TitleRuntimeService.Provider)
        {
            return _title.Value.TryQuery(*requested, out *output)
                ? RuntimeServiceResult.Success
                : RuntimeServiceResult.NoInterface;
        }

        if (RuntimeServiceRoutingPolicy.AllowsHost(*provider, *requested))
        {
            return InvokeHost(provider, requested, output);
        }

        _trace.Write($"UnsupportedProvider:{*provider:D}",
            $"interface={*requested:D}; hresult=0x80004002; localHandlesQuarantined=True");

        return RuntimeServiceResult.NoInterface;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetModuleHandleExW(uint flags, char* address, out nint module);
}
