// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Storage.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct PersistentStorageSpaceInfo
{
    public ulong AvailableFreeBytes;
    public ulong TotalFreeBytes;
    public ulong UsedBytes;
    public ulong TotalBytes;
}
