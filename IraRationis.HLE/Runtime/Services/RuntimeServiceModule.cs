// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Runtime.Services;

public static unsafe partial class RuntimeServiceModule
{
    public static HleModule Create()
    {
        return new HleModule("runtime-services",
            ["kernel32.dll", "kernelbase.dll", "api-ms-win-core-libraryloader-*"],
            [new HleExport("GetProcAddress", (nuint)(delegate* unmanaged[Stdcall]<nint, byte*, nint>)&ResolveExport)]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint ResolveExport(nint module, byte* name)
    {
        var incoming = NativeErrorState.Capture();
        var call = RuntimeBridgeDiagnostics.BeginLookup(module, (nint)name);

        incoming.Restore();

        var original = GetProcAddress(module, name);
        var outgoing = NativeErrorState.Capture();

        try
        {
            var replacement = RuntimeServiceHost.Active?.ResolveExport(module, name, original);

            return RuntimeBridgeDiagnostics.EndLookup(call, module, (nint)name, original,
                outgoing.SystemError, replacement);
        }
        catch
        {
            return original;
        }
        finally
        {
            outgoing.Restore();
        }
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcAddress(nint module, byte* name);
}
