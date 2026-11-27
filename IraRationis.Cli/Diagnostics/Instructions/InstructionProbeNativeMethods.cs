// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Cli.Diagnostics.Instructions.Models;

namespace IraRationis.Cli.Diagnostics.Instructions;

internal static partial class InstructionProbeNativeMethods
{
    internal const uint Commit = 0x1000;
    internal const uint Reserve = 0x2000;
    internal const uint Decommit = 0x4000;
    internal const uint Release = 0x8000;
    internal const uint NoAccess = 0x01;
    internal const uint ReadOnly = 0x02;
    internal const uint ReadWrite = 0x04;
    internal const uint Guard = 0x100;
    internal const uint MapCopy = 0x01;
    internal const uint MapWrite = 0x02;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualFree(nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualProtect(nint address, nuint size, uint protection, uint* previousProtection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualLock(nint address, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualUnlock(nint address, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nuint
        VirtualQuery(nint address, InstructionProbeMemoryInformation* information, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint CreateFileMappingW(nint file, nint attributes, uint protection, uint sizeHigh,
        uint sizeLow, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int UnmapViewOfFile(nint address);
}
