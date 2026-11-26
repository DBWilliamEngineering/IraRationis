// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx victory-hle/src/modules/xmem.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Memory.Models;

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal unsafe struct XMemMemoryInformation
{
    [FieldOffset(32)] public uint State;
}
