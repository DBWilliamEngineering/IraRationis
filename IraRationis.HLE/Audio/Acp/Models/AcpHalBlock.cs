// SPDX-License-Identifier: GPL-3.0-or-later
// Audio heap ABI and SHAPE context layout cross-checked against
// VIctory/victory-fork crates/victory-hle/src/modules/audio.rs.


namespace IraRationis.HLE.Audio.Acp.Models;

internal readonly unsafe record struct AcpHalBlock(nuint Host, uint Device, uint Size, bool ContextOwned);
