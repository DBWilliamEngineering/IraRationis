// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Interop;

namespace IraRationis.HLE.Runtime.Diagnostics.RuntimeBridgeCallbacks;

internal static unsafe partial class RuntimeBridgeCallbacks
{
    private static nint _loadLibrary;
    private static nint _loadLibraryAnsi;
    private static nint _loadLibraryExtended;
    private static nint _loadLibraryExtendedAnsi;
    private static nint _getProcAddress;
    private static nint _initialize;
    private static nint _initializeExtended;
    private static nint _initializeWithOptions;
    private static nint _query;

    internal static nint ObserveHostFunction(string library, string name, nint target)
    {
        if (target == 0 || !IsLoaderLibrary(library))
        {
            return target;
        }

        var errorState = NativeErrorState.Capture();

        try
        {
            return name switch
            {
                "LoadLibraryW" => Bind(ref _loadLibrary, target,
                    (nint)(delegate* unmanaged[Stdcall]<char*, nint>)&LoadLibrary, name),
                "LoadLibraryA" => Bind(ref _loadLibraryAnsi, target,
                    (nint)(delegate* unmanaged[Stdcall]<byte*, nint>)&LoadLibraryAnsi, name),
                "LoadLibraryExW" => Bind(ref _loadLibraryExtended, target,
                    (nint)(delegate* unmanaged[Stdcall]<char*, nint, uint, nint>)&LoadLibraryExtended, name),
                "LoadLibraryExA" => Bind(ref _loadLibraryExtendedAnsi, target,
                    (nint)(delegate* unmanaged[Stdcall]<byte*, nint, uint, nint>)&LoadLibraryExtendedAnsi, name),
                "GetProcAddress" => Bind(ref _getProcAddress, target,
                    (nint)(delegate* unmanaged[Stdcall]<nint, byte*, nint>)&GetProcAddress, name),
                _ => target
            };
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal static nint ObserveRuntimeExport(string name, nint target)
    {
        if (target == 0)
        {
            return target;
        }

        var errorState = NativeErrorState.Capture();

        try
        {
            return name switch
            {
                "InitializeApiImpl" => Bind(ref _initialize, target,
                    (nint)(delegate* unmanaged[Stdcall]<ulong, ulong, int>)&Initialize, name),
                "InitializeApiImplEx" => Bind(ref _initializeExtended, target,
                    (nint)(delegate* unmanaged[Stdcall]<ulong, ulong, ulong, int>)&InitializeExtended, name),
                "InitializeApiImplEx2" => Bind(ref _initializeWithOptions, target,
                    (nint)(delegate* unmanaged[Stdcall]<ulong, ulong, ulong, nint, int>)&InitializeWithOptions, name),
                "QueryApiImpl" => Bind(ref _query, target,
                    (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Query, name),
                _ => target
            };
        }
        finally
        {
            errorState.Restore();
        }
    }

    private static nint Bind(ref nint slot, nint target, nint callback, string name)
    {
        if (target == callback)
        {
            return target;
        }

        var existing = Interlocked.CompareExchange(ref slot, target, 0);

        // Cached guest function pointers retain their original target for the process lifetime.
        // A different DLL/address is left untouched; no pinning, retargeting or extra loader reference.
        var returned = existing == 0 || existing == target ? callback : target;

        if (existing == 0 || existing != target)
        {
            RuntimeBridgeDiagnostics.NoteBinding(name, target, returned);
        }

        return returned;
    }

    private static bool IsLoaderLibrary(string library)
    {
        return library.Equals("kernel32.dll", StringComparison.OrdinalIgnoreCase)
               || library.Equals("kernelbase.dll", StringComparison.OrdinalIgnoreCase)
               || library.StartsWith("api-ms-win-core-libraryloader-", StringComparison.OrdinalIgnoreCase);
    }
}
