// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal static unsafe partial class RuntimeBridgeMemoryNativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualProtect(nint address, nuint size, uint protection, out uint previousProtection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nuint VirtualQuery(nint address, RuntimeBridgeMemoryInformation* information, nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int VirtualFree(nint address, nuint size, uint freeType);
}
