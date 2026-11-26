// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI and placement rules adapted from Zettrox/ztrx victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ResourceDescription1
{
    public ResourceDescription Description;
    public uint RegionWidth;
    public uint RegionHeight;
    public uint RegionDepth;
}
