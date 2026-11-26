// SPDX-License-Identifier: GPL-3.0-or-later
// Console query heap ABI adapted from Zettrox/ztrx victory-gpu/src/query.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Queries.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct QueryHeapObject
{
    public GraphicsObjectHeader Header;
    public QueryHeapDescription Description;
}
