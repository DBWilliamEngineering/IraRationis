// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Diagnostics.Models;

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct RuntimeBridgeMemoryInformation
{
    [FieldOffset(0)] public nuint BaseAddress;
    [FieldOffset(24)] public nuint RegionSize;
    [FieldOffset(32)] public uint State;
    [FieldOffset(36)] public uint Protection;
}
