// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.


namespace IraRationis.HLE.Input.Models;

internal sealed record GameInputNotification(
    GameInputRegistration Registration,
    int Index,
    ulong Timestamp,
    uint Current,
    uint Previous);
