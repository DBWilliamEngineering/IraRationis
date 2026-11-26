// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GameInputDevice
{
    public nint Table;
    public int Index;
    public GameInputDeviceInfo Info;
}
