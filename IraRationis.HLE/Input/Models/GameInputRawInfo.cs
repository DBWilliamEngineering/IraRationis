// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Models;

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct GameInputRawInfo
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(4)] public uint Type;
    [FieldOffset(8)] public uint Vendor;
    [FieldOffset(12)] public uint Product;
    [FieldOffset(20)] public ushort UsagePage;
    [FieldOffset(22)] public ushort Usage;
}
