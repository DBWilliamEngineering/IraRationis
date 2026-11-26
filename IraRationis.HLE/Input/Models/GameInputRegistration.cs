// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/gameinput.rs.


namespace IraRationis.HLE.Input.Models;

internal sealed unsafe record GameInputRegistration(ulong Token, nint Device, uint Kind, uint Status, nint Context, nint Callback);
