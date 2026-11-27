// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Descriptors.Models;

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal unsafe struct BufferShaderResourceView
{
    [FieldOffset(0)]
    public uint Format;

    [FieldOffset(4)]
    public uint Dimension;

    [FieldOffset(8)]
    public uint Mapping;

    [FieldOffset(16)]
    public ulong FirstElement;

    [FieldOffset(24)]
    public uint Elements;
}
