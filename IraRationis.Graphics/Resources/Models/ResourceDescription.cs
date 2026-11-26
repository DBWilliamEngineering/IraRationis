// SPDX-License-Identifier: GPL-3.0-or-later
// Console resource ABI adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ResourceDescription
{
    public uint Dimension;
    public ulong Alignment;
    public ulong Width;
    public uint Height;
    public ushort DepthOrArraySize;
    public ushort MipLevels;
    public uint Format;
    public uint SampleCount;
    public uint SampleQuality;
    public uint Layout;
    public uint Flags;
}
