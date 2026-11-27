// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.


namespace IraRationis.HLE.Input.Models;

internal sealed class GameInputSlot
{
    public bool Connected;
    public ulong Sequence;

    public List<GameInputSnapshot> History
    {
        get;
    } = [];
}
