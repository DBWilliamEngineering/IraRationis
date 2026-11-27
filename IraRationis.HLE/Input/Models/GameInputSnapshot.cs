// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.


using GameInput = IraRationis.HLE.Input.GameInputModule.GameInputModule;

namespace IraRationis.HLE.Input.Models;

internal sealed record GameInputSnapshot(
    int Index,
    ulong Sequence,
    ulong Timestamp,
    GameInputPadState? Pad,
    GameInputKeyState[] Keys)
{
    public uint Kind
    {
        get
        {
            return Index == GameInput.Keyboard ? GameInput.KeyboardKind : GameInput.GamepadKind;
        }
    }
}
