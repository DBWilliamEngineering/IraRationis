// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Models;

[StructLayout(LayoutKind.Sequential)]
public record struct GameInputPadState(
    uint Buttons,
    float LeftTrigger,
    float RightTrigger,
    float LeftX,
    float LeftY,
    float RightX,
    float RightY);
