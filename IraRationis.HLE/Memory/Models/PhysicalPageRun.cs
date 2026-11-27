// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/xmem.rs.


namespace IraRationis.HLE.Memory.Models;

internal sealed record PhysicalPageRun(PhysicalPageSlice First, ulong Count);
