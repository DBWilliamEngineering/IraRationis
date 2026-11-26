// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal static unsafe partial class RuntimeBridgeCallbacks
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Initialize(ulong firstVersion, ulong secondVersion)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginInitialization(RuntimeBridgeOperation.Initialize,
            firstVersion, secondVersion);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<ulong, ulong, int>)_initialize)(firstVersion, secondVersion);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndResult(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InitializeExtended(ulong firstVersion, ulong secondVersion, ulong flags)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginInitialization(RuntimeBridgeOperation.InitializeEx,
            firstVersion, secondVersion, flags);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<ulong, ulong, ulong, int>)_initializeExtended)(
            firstVersion, secondVersion, flags);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndResult(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InitializeWithOptions(ulong firstVersion, ulong secondVersion, ulong flags, nint options)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginInitialization(RuntimeBridgeOperation.InitializeEx2,
            firstVersion, secondVersion, flags, options);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<ulong, ulong, ulong, nint, int>)_initializeWithOptions)(
            firstVersion, secondVersion, flags, options);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndResult(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(nint provider, nint requestedInterface, nint output)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginQuery(provider, requestedInterface, output);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)_query)(provider, requestedInterface, output);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndResult(call, result, outgoing.SystemError, output);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }
}
