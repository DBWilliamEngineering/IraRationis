// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI and placement rules adapted from Zettrox/ztrx victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct CopyFootprint
{
    public ulong Offset;
    public uint Format;
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint RowPitch;
}
