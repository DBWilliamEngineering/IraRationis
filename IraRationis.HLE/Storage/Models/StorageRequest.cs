// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Storage.Models;

[StructLayout(LayoutKind.Explicit, Size = 0x48)]
internal unsafe struct StorageRequest
{
    [FieldOffset(0)] public ulong Options;
    [FieldOffset(8)] public nint Destination;
    [FieldOffset(16)] public ushort DestinationPageOffset;
    [FieldOffset(24)] public uint DestinationSize;
    [FieldOffset(32)] public nint Source;
    [FieldOffset(40)] public ulong Offset;
    [FieldOffset(48)] public uint SourceSize;
    [FieldOffset(52)] public uint IntermediateSize;
    [FieldOffset(56)] public ulong Tag;
    [FieldOffset(64)] public nint Name;
}
