// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Models;

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal unsafe struct GameInputHidCaps
{
    [FieldOffset(0)] public ushort Usage;
    [FieldOffset(2)] public ushort UsagePage;
    [FieldOffset(4)] public ushort InputReportBytes;
}
