// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Storage.Models;

[StructLayout(LayoutKind.Explicit, Size = 0x20)]
internal unsafe struct StorageQueueDescription
{
    [FieldOffset(0)] public ulong SourceType;
    [FieldOffset(8)] public ushort Capacity;
    [FieldOffset(10)] public sbyte Priority;
    [FieldOffset(16)] public nint Name;
    [FieldOffset(24)] public ulong Options;
}
