// SPDX-License-Identifier: GPL-3.0-or-later
// Console synchronization signatures from Zettrox/ztrx and DexNom/VIctory victory-gpu/src/list.rs.

using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.Synchronization.Models;

// The SDK union has a pointer-sized member at offset 8 on x64.
// Keep its representation explicit instead of depending on a generated union member name.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GlobalBarrierGroup
{
    public D3D12_BARRIER_TYPE Type;
    public uint Count;
    public D3D12_GLOBAL_BARRIER* Barriers;
}
