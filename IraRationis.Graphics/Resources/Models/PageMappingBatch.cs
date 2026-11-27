// SPDX-License-Identifier: GPL-3.0-or-later
// Queue ABI and range kinds cross-checked against DexNom/VIctory, victory-gpu/src/queue.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PageMappingBatch
{
    public ulong Destination;
    public uint Count;
    public ulong Ranges;
}
