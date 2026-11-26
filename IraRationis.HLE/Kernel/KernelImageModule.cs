// SPDX-License-Identifier: GPL-3.0-or-later
// Title-aware GetModuleHandle and GetModuleFileName overrides.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Kernel;

public static unsafe partial class KernelImageModule
{
    public static HleModule Create()
    {
        return new HleModule("images", ["kernel32.dll", "kernelbase.dll", "api-ms-win-core-libraryloader-*"],
        [
            new HleExport("GetModuleHandleW", (nuint)(delegate* unmanaged[Stdcall]<char*, nint>)&GetTitleModuleW),
            new HleExport("GetModuleHandleA", (nuint)(delegate* unmanaged[Stdcall]<byte*, nint>)&GetTitleModuleA),
            new HleExport("GetModuleFileNameW",
                (nuint)(delegate* unmanaged[Stdcall]<nint, char*, uint, uint>)&GetTitlePathW),
            new HleExport("GetModuleFileNameA",
                (nuint)(delegate* unmanaged[Stdcall]<nint, byte*, uint, uint>)&GetTitlePathA)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint GetTitleModuleW(char* name)
    {
        try
        {
            if (name == null)
            {
                return (nint)(RuntimeState.MainImage ?? 0);
            }

            var file = Path.GetFileName(new string(name));
            var address = RuntimeState.FindImage(file);

            return address.HasValue ? (nint)address.Value : GetModuleHandleW(name);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint GetTitleModuleA(byte* name)
    {
        try
        {
            if (name == null)
            {
                return (nint)(RuntimeState.MainImage ?? 0);
            }

            var file = Path.GetFileName(Marshal.PtrToStringAnsi((nint)name) ?? string.Empty);
            var address = RuntimeState.FindImage(file);

            return address.HasValue ? (nint)address.Value : GetModuleHandleA(name);
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint GetTitlePathW(nint module, char* buffer, uint size)
    {
        try
        {
            var path = RuntimeState.GetImagePath(module == 0 ? RuntimeState.MainImage ?? 0 : (nuint)module);

            if (path is null)
            {
                return GetModuleFileNameW(module, buffer, size);
            }

            if (size == 0 || buffer == null)
            {
                return 0;
            }

            var length = Math.Min(path.Length, checked((int)size) - 1);

            path.AsSpan(0, length).CopyTo(new Span<char>(buffer, length));

            buffer[length] = '\0';

            if (length < path.Length)
            {
                SetLastError(122);

                return size;
            }

            return (uint)length;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint GetTitlePathA(nint module, byte* buffer, uint size)
    {
        try
        {
            var path = RuntimeState.GetImagePath(module == 0 ? RuntimeState.MainImage ?? 0 : (nuint)module);

            if (path is null)
            {
                return GetModuleFileNameA(module, buffer, size);
            }

            if (size == 0 || buffer == null)
            {
                return 0;
            }

            // CP_ACP preserves Win32 ANSI semantics rather than assuming UTF-8.
            fixed (char* text = path)
            {
                var required = WideCharToMultiByte(0, 0, text, path.Length, null, 0, null, null);

                if (required == 0 && path.Length > 0)
                {
                    return 0;
                }

                var bytes = new byte[required];

                fixed (byte* output = bytes)
                {
                    if (required > 0 && WideCharToMultiByte(0, 0, text, path.Length, output, required, null, null)
                        != required)
                    {
                        return 0;
                    }
                }

                var length = Math.Min(required, checked((int)size) - 1);

                bytes.AsSpan(0, length).CopyTo(new Span<byte>(buffer, length));

                buffer[length] = 0;

                if (length < required)
                {
                    SetLastError(122);

                    return size;
                }

                return (uint)length;
            }
        }
        catch
        {
            return 0;
        }
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleW(char* name);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandleA(byte* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetModuleFileNameW(nint module, char* path, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetModuleFileNameA(nint module, byte* path, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WideCharToMultiByte(uint codePage, uint flags, char* wide, int wideLength, byte* output,
        int outputLength, byte* defaultChar, int* usedDefault);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SetLastError(uint error);
}
