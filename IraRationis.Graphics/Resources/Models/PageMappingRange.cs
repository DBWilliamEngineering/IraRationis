// SPDX-License-Identifier: GPL-3.0-or-later
// Queue ABI and range kinds cross-checked against DexNom/VIctory, victory-gpu/src/queue.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PageMappingRange
{
    public uint Kind;
    public uint Start;
    public uint Count;
}
