// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Textures.Models;

// Host-only placed footprints. The caller owns actual resource identities,
// native heap types, resource states, synchronization and subresource bounds.
// This helper never infers guest layout or authorizes a guest memory access.
internal readonly record struct HostTextureTransferFootprint(
    uint Subresource,
    uint Format,
    uint Width,
    uint Height,
    uint RowPitch,
    ulong Offset);
