// SPDX-License-Identifier: GPL-3.0-or-later
// Console resource ABI adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AllocationInfo
{
    public ulong Size;
    public ulong Alignment;
}
