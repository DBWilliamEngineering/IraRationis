// SPDX-License-Identifier: GPL-3.0-or-later
// Console resource ABI adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HeapDescription
{
    public ulong Size;
    public GraphicsHeapProperties Properties;
    public ulong Alignment;
    public uint Flags;
}
