// SPDX-License-Identifier: GPL-3.0-or-later
// Explicit thread notifications outside the Windows loader lock.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Loader;

namespace IraRationis.HLE;

public static unsafe partial class KernelThreadModule
{
    private static TitleThreads? _threads;

    public static HleModule Create(TitleThreads threads)
    {
        _threads = threads;

        return new HleModule("threads",
            ["kernel32.dll", "kernelbase.dll", "api-ms-win-core-processthreads-*", "api-ms-win-core-libraryloader-*"],
            [
                new HleExport("CreateThread",
                    (nuint)(delegate* unmanaged[Stdcall]<nint, nuint, nint, nint, uint, uint*, nint>)
                    &CreateTitleThread),
                new HleExport("DisableThreadLibraryCalls",
                    (nuint)(delegate* unmanaged[Stdcall]<nint, int>)&DisableThreadCalls)
            ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint CreateTitleThread(nint attributes, nuint stack, nint start, nint parameter, uint flags,
        uint* id)
    {
        try
        {
            if (start == 0)
            {
                SetLastError(87);

                return 0;
            }

            var state = GCHandle.Alloc(new ThreadStart(start, parameter));
            var result = CreateThread(attributes, stack, &ThreadEntry, GCHandle.ToIntPtr(state), flags, id);

            if (result == 0)
            {
                var error = Marshal.GetLastPInvokeError();

                state.Free();

                SetLastError((uint)error);
            }

            return result;
        }
        catch
        {
            SetLastError(8);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ThreadEntry(nint parameter)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(parameter);
            var state = (ThreadStart)handle.Target!;

            handle.Free();

            _threads!.NotifyThread(true);

            try
            {
                var routine = (delegate* unmanaged[Stdcall]<nint, uint>)state.Routine;

                return routine(state.Parameter);
            }
            finally
            {
                _threads!.NotifyThread(false);
            }
        }
        catch (Exception exception)
        {
            Environment.FailFast("Native title thread failed.", exception);

            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisableThreadCalls(nint module)
    {
        try
        {
            var result = _threads!.DisableThreadCalls((nuint)module);

            if (result.HasValue)
            {
                if (!result.Value)
                {
                    SetLastError(87);
                }

                return result.Value ? 1 : 0;
            }

            return DisableThreadLibraryCalls(module);
        }
        catch
        {
            SetLastError(87);

            return 0;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateThread(nint attributes, nuint stack,
        delegate* unmanaged[Stdcall]<nint, uint> start, nint parameter, uint flags, uint* id);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DisableThreadLibraryCalls(nint module);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SetLastError(uint error);

    private sealed record ThreadStart(nint Routine, nint Parameter);
}
