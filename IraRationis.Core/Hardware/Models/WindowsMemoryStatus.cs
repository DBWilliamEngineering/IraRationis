// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Core.Hardware.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsMemoryStatus
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhysicalBytes;
    public ulong AvailablePhysicalBytes;
    public ulong TotalPageFileBytes;
    public ulong AvailablePageFileBytes;
    public ulong TotalVirtualBytes;
    public ulong AvailableVirtualBytes;
    public ulong AvailableExtendedVirtualBytes;
}
