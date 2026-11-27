// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Models;

[StructLayout(LayoutKind.Explicit, Size = 320)]
internal unsafe struct GameInputDeviceInfo
{
    [FieldOffset(0)] public uint Size;
    [FieldOffset(32)] public fixed byte Id[32];
    [FieldOffset(64)] public fixed byte RootId[32];
    [FieldOffset(104)] public uint SupportedInput;
    [FieldOffset(108)] public uint RumbleMotors;
}
