// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI and placement rules adapted from Zettrox/ztrx victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct AllocationInfo1
{
    public ulong Offset;
    public ulong Alignment;
    public ulong Size;
}
