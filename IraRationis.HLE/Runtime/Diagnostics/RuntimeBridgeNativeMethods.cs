// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal static unsafe partial class RuntimeBridgeNativeMethods
{
    internal static string? GetModulePath(nint module)
    {
        Span<char> path = stackalloc char[512];

        fixed (char* destination = path)
        {
            var length = GetModuleFileNameW(module, destination, (uint)path.Length);

            return length is > 0 && length < path.Length ? new string(path[..(int)length]) : null;
        }
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetModuleFileNameW(nint module, char* path, uint capacity);
}
