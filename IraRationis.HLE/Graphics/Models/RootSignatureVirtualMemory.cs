// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-gpu/src/root.rs serialization entry points.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Graphics.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RootSignatureVirtualMemory
{
    public uint Flags;
    public uint PageTablePages;
    public uint PreservedBytes;
    public ulong Lowest;
    public ulong Highest;
}
