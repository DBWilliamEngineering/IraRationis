// SPDX-License-Identifier: GPL-3.0-or-later
// Console resource ABI adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct HeapObject
{
    public GraphicsObjectHeader Header;
    public HeapDescription Description;
    public nint Memory;
}
