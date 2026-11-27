// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.Media.Models;

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct TextureCopyLocation
{
    [FieldOffset(0)] public nint Resource;
    [FieldOffset(8)] public uint Type;
    [FieldOffset(16)] public D3D12_PLACED_SUBRESOURCE_FOOTPRINT Footprint;
    [FieldOffset(16)] public uint Subresource;
}
