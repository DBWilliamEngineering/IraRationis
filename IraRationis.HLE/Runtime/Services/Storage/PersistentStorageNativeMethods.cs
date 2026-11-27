// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Storage;

internal static partial class PersistentStorageNativeMethods
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int GetDiskFreeSpace(string directory, out ulong availableBytes, out ulong totalBytes,
        out ulong totalFreeBytes);
}
