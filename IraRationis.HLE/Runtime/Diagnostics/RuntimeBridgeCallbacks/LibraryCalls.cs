// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;

namespace IraRationis.HLE.Runtime.Diagnostics.RuntimeBridgeCallbacks;

internal static unsafe partial class RuntimeBridgeCallbacks
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadLibrary(char* name)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginLoad((nint)name, true, 0);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<char*, nint>)_loadLibrary)(name);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndLoad(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadLibraryAnsi(byte* name)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginLoad((nint)name, false, 0);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<byte*, nint>)_loadLibraryAnsi)(name);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndLoad(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadLibraryExtended(char* name, nint file, uint flags)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginLoad((nint)name, true, flags);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)_loadLibraryExtended)(name, file, flags);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndLoad(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LoadLibraryExtendedAnsi(byte* name, nint file, uint flags)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginLoad((nint)name, false, flags);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<byte*, nint, uint, nint>)_loadLibraryExtendedAnsi)(name, file, flags);
        var outgoing = NativeErrorState.Capture();

        try
        {
            RuntimeBridgeDiagnostics.EndLoad(call, result, outgoing.SystemError);

            return result;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint GetProcAddress(nint module, byte* name)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginLookup(module, (nint)name);

        incoming.Restore();

        var result = ((delegate* unmanaged[Stdcall]<nint, byte*, nint>)_getProcAddress)(module, name);
        var outgoing = NativeErrorState.Capture();

        try
        {
            return RuntimeBridgeDiagnostics.EndLookup(call, module, (nint)name, result, outgoing.SystemError);
        }
        finally
        {
            outgoing.Restore();
        }
    }
}
