// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Media.Models;

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct VideoPlaneView
{
    [FieldOffset(0)] public uint Format;
    [FieldOffset(4)] public uint Dimension;
    [FieldOffset(8)] public uint Mapping;
    [FieldOffset(20)] public uint MipLevels;
    [FieldOffset(24)] public uint PlaneSlice;
}
