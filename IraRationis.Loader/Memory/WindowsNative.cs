// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using IraRationis.Loader.Memory.Models;

namespace IraRationis.Loader.Memory;

internal static unsafe partial class WindowsNative
{
    internal const uint ReadWrite = 4;
    internal const uint ExecuteRead = 0x20;
    internal const uint ExecuteReadWrite = 0x40;
    internal const uint CommitReserve = 0x3000;
    internal const uint Release = 0x8000;

    internal static void Check(int result, string operation)
    {
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), operation);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint LoadLibraryExW(string name, nint file, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int FreeLibrary(nint module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint GetProcAddress(nint module, byte* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint VirtualAlloc(nint address, nuint size, uint type, uint protection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualFree(nint address, nuint size, uint type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualProtect(nint address, nuint size, uint protection, uint* previous);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int FlushInstructionCache(nint process, nint address, nuint size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nuint VirtualQuery(nint address, WindowsNativeMemoryInformation* information, nuint length);
}
