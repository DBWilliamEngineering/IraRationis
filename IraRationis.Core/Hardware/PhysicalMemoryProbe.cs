// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Core.Hardware.Models;

namespace IraRationis.Core.Hardware;

internal static partial class PhysicalMemoryProbe
{
    internal static bool TryCapture(out WindowsMemoryStatus memoryStatus)
    {
        memoryStatus = new WindowsMemoryStatus
        {
            Length = (uint)Marshal.SizeOf<WindowsMemoryStatus>()
        };

        return OperatingSystem.IsWindows() && GlobalMemoryStatusEx(ref memoryStatus);
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref WindowsMemoryStatus memoryStatus);
}
