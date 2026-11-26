// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics;
using IraRationis.PE.Models;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal static unsafe class RuntimeBridgeDiagnosticsTests
{
    private const ulong FirstVersion = 0xFEDCBA9876543210;
    private const ulong SecondVersion = 0x81234567ABCDEF01;
    private const ulong InitializationFlags = 0xCAFEFACE12345678;
    private const int FailureResult = unchecked((int)0x89240107);

    internal static void Run()
    {
        OptionsRequireExplicitEnablement();

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.WriteLine("Runtime bridge unmanaged forwarding contracts require Windows x64; skipped.");

            return;
        }

        var originalErrors = NativeErrorState.Capture();

        try
        {
            SessionOwnershipIsExplicit();
            RuntimeBridgeAbiPolicyTests.Run();
            BindingPreservesUnknownAndConflictingTargets();
            InitializationPreservesFullWidthArguments();
            QueryPreservesPointersOutputsAndResults();
            LoaderCallsPreserveArgumentsAndOrdinals();
            WriterFailuresCannotChangeTargetCalls();
            DiagnosticLimitsDoNotSuppressTargetCalls();
            CachedCallbacksKeepForwardingAfterDisposal();
            RuntimeBridgeMemoryReaderTests.Run();
        }
        finally
        {
            originalErrors.Restore();
        }

        Console.WriteLine("Authored runtime bridge forwarding, error-state, and lifetime contracts passed.");
    }

    private static void OptionsRequireExplicitEnablement()
    {
        Require(RuntimeBridgeOptions.FromEnvironment(_ => null) == new RuntimeBridgeOptions(),
            "Runtime bridge tracing must default to disabled.");

        foreach (var value in new[] { "", "0", "true", "01", "1 ", " 1" })
        {
            Require(!RuntimeBridgeOptions.FromEnvironment(_ => value).Enabled,
                "Runtime bridge tracing must require the exact environment value 1.");
        }

        Require(RuntimeBridgeOptions.FromEnvironment(name => name == "IRX_RUNTIME_BRIDGE_TRACE" ? "1" : null).Enabled,
            "The runtime bridge environment switch must enable the diagnostic explicitly.");

        using var writer = new DisruptiveRuntimeBridgeWriter(true);

        Require(RuntimeBridgeDiagnostics.Begin(writer, new RuntimeBridgeOptions()) is null && writer.Writes == 0,
            "Disabled diagnostics must neither create a session nor touch the diagnostic writer.");
    }

    private static void SessionOwnershipIsExplicit()
    {
        foreach (var limit in new[] { 0, -1, 4097 })
        {
            try
            {
                using var unexpected = RuntimeBridgeDiagnostics.Begin(TextWriter.Null, new RuntimeBridgeOptions(true, limit));

                throw new InvalidOperationException("An invalid runtime bridge record limit was accepted.");
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        using var session = Begin(TextWriter.Null);
        var rejected = false;

        try
        {
            using var duplicate = Begin(TextWriter.Null);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Require(rejected, "Concurrent diagnostic sessions must not replace the active session.");
    }

    private static void BindingPreservesUnknownAndConflictingTargets()
    {
        using var writer = new StringWriter();
        using var session = Begin(writer);
        var original = (nuint)AuthoredRuntimeBridgeTargets.LoadLibraryAddress;
        var symbol = new ImportSymbol("LoadLibraryExW", 0);

        Require(session.Observe("unrelated.dll", symbol, original) == original,
            "An unrelated module's same-named function must remain untouched.");
        Require(session.Observe("kernel32.dll", new ImportSymbol(null, 1), original) == original,
            "An ordinal host import must remain untouched.");
        Require(session.Observe("kernel32.dll", new ImportSymbol("UnobservedExport", 0), original) == original,
            "An unobserved host export must remain untouched.");
        Require(session.Observe("kernel32.dll", symbol, 0) == 0,
            "A failed host resolution must remain a null address.");

        var observed = session.Observe("kernel32.dll", symbol, original);

        Require(observed != original && observed != 0, "An enabled loader diagnostic must return its forwarding callback.");
        Require(session.Observe("API-MS-WIN-CORE-LIBRARYLOADER-L1-2-0.DLL", symbol, original) == observed,
            "An API-set alias for the same target must reuse the forwarding callback.");
        Require(session.Observe("kernelbase.dll", symbol, observed) == observed,
            "A previously wrapped pointer must not be wrapped recursively.");

        var conflicting = (nuint)AuthoredRuntimeBridgeTargets.AlternateLoadLibraryAddress;

        Require(session.Observe("kernelbase.dll", symbol, conflicting) == conflicting,
            "A conflicting alias target must be returned unchanged instead of retargeting cached callbacks.");
        Require(session.Observe("kernel32.dll", symbol, original) == observed,
            "A conflicting alias must not displace the original binding.");

        foreach (var name in new[] { "XErrorReport", "UninitializeApiImpl", "XUserAddAsync", "initializeapiimpl" })
        {
            Require(RuntimeBridgeCallbacks.ObserveRuntimeExport(name, AuthoredRuntimeBridgeTargets.InitializeAddress)
                    == AuthoredRuntimeBridgeTargets.InitializeAddress,
                "Unknown runtime exports and case mismatches must retain their exact address.");
        }

        Require(RuntimeBridgeCallbacks.ObserveRuntimeExport("QueryApiImpl", 0) == 0,
            "A missing runtime export must remain missing.");
    }

    private static void InitializationPreservesFullWidthArguments()
    {
        using var writer = new StringWriter();
        using var session = Begin(writer);
        var initialize = (delegate* unmanaged[Stdcall]<ulong, ulong, int>)RuntimeBridgeCallbacks.ObserveRuntimeExport(
            "InitializeApiImpl", AuthoredRuntimeBridgeTargets.InitializeAddress);
        var initializeExtended = (delegate* unmanaged[Stdcall]<ulong, ulong, ulong, int>)RuntimeBridgeCallbacks.ObserveRuntimeExport(
            "InitializeApiImplEx", AuthoredRuntimeBridgeTargets.InitializeExtendedAddress);
        var initializeWithOptions = (delegate* unmanaged[Stdcall]<ulong, ulong, ulong, nint, int>)RuntimeBridgeCallbacks.ObserveRuntimeExport(
            "InitializeApiImplEx2", AuthoredRuntimeBridgeTargets.InitializeWithOptionsAddress);

        foreach (var expectedResult in new[] { 0, 1, FailureResult })
        {
            AuthoredRuntimeBridgeTargets.Configure(expectedResult);
            AuthoredRuntimeBridgeTargets.SetIncomingErrors();
            var result = initialize(FirstVersion, SecondVersion);

            RequireTargetErrors();
            Require(result == expectedResult && AuthoredRuntimeBridgeTargets.FirstVersion == FirstVersion
                    && AuthoredRuntimeBridgeTargets.SecondVersion == SecondVersion,
                "InitializeApiImpl must forward both complete version values and the exact HRESULT.");

            AuthoredRuntimeBridgeTargets.Configure(expectedResult);
            AuthoredRuntimeBridgeTargets.SetIncomingErrors();
            result = initializeExtended(FirstVersion, SecondVersion, InitializationFlags);

            RequireTargetErrors();
            Require(result == expectedResult && AuthoredRuntimeBridgeTargets.FirstVersion == FirstVersion
                    && AuthoredRuntimeBridgeTargets.SecondVersion == SecondVersion
                    && AuthoredRuntimeBridgeTargets.Flags == InitializationFlags,
                "InitializeApiImplEx must preserve all 64 bits of each argument.");

            AuthoredRuntimeBridgeTargets.Configure(expectedResult);
            AuthoredRuntimeBridgeTargets.SetIncomingErrors();
            result = initializeWithOptions(FirstVersion, SecondVersion, InitializationFlags, 1);

            RequireTargetErrors();
            Require(result == expectedResult && AuthoredRuntimeBridgeTargets.FirstVersion == FirstVersion
                    && AuthoredRuntimeBridgeTargets.SecondVersion == SecondVersion
                    && AuthoredRuntimeBridgeTargets.Flags == InitializationFlags && AuthoredRuntimeBridgeTargets.Options == 1,
                "InitializeApiImplEx2 must preserve its full-width arguments and forward an opaque, unreadable options pointer.");
        }

        Require(writer.ToString().Contains("version1=0xFEDCBA9876543210", StringComparison.Ordinal)
                && writer.ToString().Contains("flags=0xCAFEFACE12345678", StringComparison.Ordinal),
            "Initialization records must retain full-width argument evidence.");
    }

    private static void QueryPreservesPointersOutputsAndResults()
    {
        using var writer = new StringWriter();
        using var session = Begin(writer);
        var query = QueryCallback();
        var provider = new Guid("daf2c6c2-76ce-4c33-a6d0-c4498ed73762");
        var requestedInterface = new Guid("641faed9-0552-47cf-8c0d-f679b55d90ab");
        var expectedObject = unchecked((nint)0x123456789ABC);

        foreach (var expectedResult in new[] { 0, 1, FailureResult })
        {
            nint output = 0x1234;
            AuthoredRuntimeBridgeTargets.Configure(expectedResult, expectedObject, writeOutput: expectedResult >= 0);
            AuthoredRuntimeBridgeTargets.SetIncomingErrors();
            var result = query((nint)(&provider), (nint)(&requestedInterface), (nint)(&output));

            RequireTargetErrors();
            Require(result == expectedResult && AuthoredRuntimeBridgeTargets.Provider == (nint)(&provider)
                    && AuthoredRuntimeBridgeTargets.RequestedInterface == (nint)(&requestedInterface)
                    && AuthoredRuntimeBridgeTargets.Output == (nint)(&output),
                "QueryApiImpl must forward the original three pointer arguments and exact HRESULT.");
            Require(output == (expectedResult >= 0 ? expectedObject : 0x1234),
                "QueryApiImpl must preserve the target's output and must not initialize a failed query's untouched output.");
        }

        AuthoredRuntimeBridgeTargets.Configure(FailureResult);
        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        var nullResult = query(0, 0, 0);

        RequireTargetErrors();
        Require(nullResult == FailureResult && AuthoredRuntimeBridgeTargets.Provider == 0
                && AuthoredRuntimeBridgeTargets.RequestedInterface == 0 && AuthoredRuntimeBridgeTargets.Output == 0,
            "Null query pointers must reach the target unchanged for its own validation.");
        Require(provider == new Guid("daf2c6c2-76ce-4c33-a6d0-c4498ed73762")
                && requestedInterface == new Guid("641faed9-0552-47cf-8c0d-f679b55d90ab"),
            "Diagnostic GUID reads must not modify the caller's identifiers.");
        Require(writer.ToString().Contains(provider.ToString("D"), StringComparison.Ordinal)
                && writer.ToString().Contains(requestedInterface.ToString("D"), StringComparison.Ordinal)
                && writer.ToString().Contains("returnedObject=0x123456789ABC", StringComparison.Ordinal),
            "Query records must identify readable GUIDs and the successful raw returned object.");
    }

    private static void LoaderCallsPreserveArgumentsAndOrdinals()
    {
        using var writer = new StringWriter();
        using var session = Begin(writer);
        var load = (delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)session.Observe("kernel32.dll",
            new ImportSymbol("LoadLibraryExW", 0), (nuint)AuthoredRuntimeBridgeTargets.LoadLibraryAddress);
        var lookup = (delegate* unmanaged[Stdcall]<nint, byte*, nint>)session.Observe("kernel32.dll",
            new ImportSymbol("GetProcAddress", 0), (nuint)AuthoredRuntimeBridgeTargets.LookupAddress);
        const uint flags = 0xFEDC0123;
        var file = unchecked((nint)0x123456789ABC);

        fixed (char* name = "C:\\authored\\XGameRuntime.dll")
        {
            foreach (var expectedModule in new nint[] { 0, 0x12345678 })
            {
                AuthoredRuntimeBridgeTargets.Configure(pointerResult: expectedModule);
                AuthoredRuntimeBridgeTargets.SetIncomingErrors();
                var result = load(name, file, flags);

                RequireTargetErrors();
                Require(result == expectedModule && AuthoredRuntimeBridgeTargets.Name == (nint)name
                        && AuthoredRuntimeBridgeTargets.File == file && AuthoredRuntimeBridgeTargets.Flags == flags,
                    "LoadLibraryExW must forward name, file, flags, and both success and failure results without loading a real runtime.");
            }
        }

        foreach (var ordinal in new[] { 0, 1, ushort.MaxValue })
        {
            AuthoredRuntimeBridgeTargets.Configure(pointerResult: 0x12345678);
            AuthoredRuntimeBridgeTargets.SetIncomingErrors();
            var result = lookup(0, (byte*)ordinal);

            RequireTargetErrors();
            Require(result == 0x12345678 && AuthoredRuntimeBridgeTargets.Module == 0
                    && AuthoredRuntimeBridgeTargets.Name == ordinal,
                "GetProcAddress must forward ordinal pointers without reading them as strings or replacing the result.");
        }
    }

    private static void WriterFailuresCannotChangeTargetCalls()
    {
        foreach (var throwOnWrite in new[] { false, true })
        {
            foreach (var queryOperation in new[] { false, true })
            {
                using var writer = new DisruptiveRuntimeBridgeWriter(false);
                using var session = Begin(writer);
                var callback = queryOperation ? RuntimeBridgeCallbacks.ObserveRuntimeExport("QueryApiImpl", AuthoredRuntimeBridgeTargets.QueryAddress)
                    : RuntimeBridgeCallbacks.ObserveRuntimeExport("InitializeApiImplEx2", AuthoredRuntimeBridgeTargets.InitializeWithOptionsAddress);
                writer.ThrowOnWrite = throwOnWrite;
                var previousWrites = writer.Writes;
                AuthoredRuntimeBridgeTargets.Configure(FailureResult);
                AuthoredRuntimeBridgeTargets.SetIncomingErrors();
                var result = queryOperation
                    ? ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)callback)(0, 0, 0)
                    : ((delegate* unmanaged[Stdcall]<ulong, ulong, ulong, nint, int>)callback)(FirstVersion, SecondVersion, InitializationFlags, 1);

                RequireTargetErrors();
                Require(result == FailureResult && writer.Writes == previousWrites + 2,
                    "Mutating or throwing diagnostic writers must not alter target invocation, HRESULT, or either error state.");
            }
        }
    }

    private static void DiagnosticLimitsDoNotSuppressTargetCalls()
    {
        using var writer = new StringWriter();
        using var session = RuntimeBridgeDiagnostics.Begin(writer, new RuntimeBridgeOptions(true, 2))
            ?? throw new InvalidOperationException("Enabled authored bridge session was not created.");
        var initialize = (delegate* unmanaged[Stdcall]<ulong, ulong, int>)RuntimeBridgeCallbacks.ObserveRuntimeExport(
            "InitializeApiImpl", AuthoredRuntimeBridgeTargets.InitializeAddress);
        AuthoredRuntimeBridgeTargets.Configure();

        for (var callIndex = 0; callIndex < 40; callIndex++)
        {
            _ = initialize(FirstVersion, SecondVersion);
        }

        session.Dispose();

        var records = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Require(AuthoredRuntimeBridgeTargets.Calls == 40
                && records.Count(record => record.Contains("phase=begin operation=Initialize ", StringComparison.Ordinal)) == 2
                && records.Count(record => record.Contains("phase=end operation=Initialize ", StringComparison.Ordinal)) == 2
                && records.Any(record => record.Contains("phase=summary operation=Initialize ", StringComparison.Ordinal)
                    && record.Contains("started=40 completed=40 callsBeyondLimit=38", StringComparison.Ordinal)),
            "A diagnostic record limit must bound output while every target call and completion remains counted.");
    }

    private static void CachedCallbacksKeepForwardingAfterDisposal()
    {
        using var writer = new StringWriter();
        using var session = Begin(writer);
        var callback = RuntimeBridgeCallbacks.ObserveRuntimeExport("InitializeApiImpl", AuthoredRuntimeBridgeTargets.InitializeAddress);
        session.Dispose();
        var recordsAfterDisposal = writer.ToString();

        AuthoredRuntimeBridgeTargets.Configure(FailureResult);
        AuthoredRuntimeBridgeTargets.SetIncomingErrors();
        var result = ((delegate* unmanaged[Stdcall]<ulong, ulong, int>)callback)(FirstVersion, SecondVersion);

        RequireTargetErrors();
        session.Dispose();

        Require(result == FailureResult && writer.ToString() == recordsAfterDisposal,
            "Cached callbacks must retain their original target after disposal without emitting further diagnostic records.");
        Require(session.Observe("kernel32.dll", new ImportSymbol("LoadLibraryExW", 0),
                    (nuint)AuthoredRuntimeBridgeTargets.LoadLibraryAddress) == (nuint)AuthoredRuntimeBridgeTargets.LoadLibraryAddress,
            "A disposed observer must stop creating or returning diagnostic bindings.");
    }

    private static delegate* unmanaged[Stdcall]<nint, nint, nint, int> QueryCallback()
    {
        return (delegate* unmanaged[Stdcall]<nint, nint, nint, int>)RuntimeBridgeCallbacks.ObserveRuntimeExport(
            "QueryApiImpl", AuthoredRuntimeBridgeTargets.QueryAddress);
    }

    private static RuntimeBridgeDiagnostics Begin(TextWriter writer)
    {
        return RuntimeBridgeDiagnostics.Begin(writer, new RuntimeBridgeOptions(true))
            ?? throw new InvalidOperationException("Enabled authored bridge session was not created.");
    }

    private static void RequireTargetErrors()
    {
        var systemError = Marshal.GetLastSystemError();
        var invocationError = Marshal.GetLastPInvokeError();

        Require(systemError == AuthoredRuntimeBridgeTargets.OutgoingSystemError
                && invocationError == AuthoredRuntimeBridgeTargets.OutgoingInvocationError,
            "The observer must preserve the target's native and managed outgoing error states.");
        Require(AuthoredRuntimeBridgeTargets.Calls == 1
                && AuthoredRuntimeBridgeTargets.ObservedSystemError == AuthoredRuntimeBridgeTargets.IncomingSystemError
                && AuthoredRuntimeBridgeTargets.ObservedInvocationError == AuthoredRuntimeBridgeTargets.IncomingInvocationError,
            "The observer must call the target exactly once with both incoming error states intact.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
