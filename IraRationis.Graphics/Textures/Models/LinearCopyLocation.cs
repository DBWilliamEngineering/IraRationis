// SPDX-License-Identifier: GPL-3.0-or-later
// Independent linear copy implementation using documented Windows D3D12 contracts.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Textures.Models;

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct LinearCopyLocation
{
    [FieldOffset(0)] public nint Resource;
    [FieldOffset(8)] public uint Kind;
    [FieldOffset(16)] public uint SubresourceIndex;
    [FieldOffset(16)] public ulong Offset;
    [FieldOffset(24)] public uint Format;
    [FieldOffset(28)] public uint Width;
    [FieldOffset(32)] public uint Height;
    [FieldOffset(36)] public uint Depth;
    [FieldOffset(40)] public uint Pitch;
}
