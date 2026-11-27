// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.
// Ordered per-queue workers; weighted shared scheduling and BCPack are not yet ported.


namespace IraRationis.HLE.Storage.Models;

internal sealed record StorageCommand(StorageRequest Request, nint File, nint Status, uint Index);
