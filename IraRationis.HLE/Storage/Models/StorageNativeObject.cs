// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/dstorage.rs.
// Ordered per-queue workers; weighted shared scheduling and BCPack are not yet ported.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Storage.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct StorageNativeObject
{
    public nint Table;
    public nint State;
    public int References;
}
