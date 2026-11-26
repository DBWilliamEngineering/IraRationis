// SPDX-License-Identifier: GPL-3.0-or-later
// Console query heap ABI adapted from Zettrox/ztrx victory-gpu/src/query.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Queries.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct QueryHeapDescription
{
    public uint Kind;
    public uint Count;
    public uint Nodes;
}
