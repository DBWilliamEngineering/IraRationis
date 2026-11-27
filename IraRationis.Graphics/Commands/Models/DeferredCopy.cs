// SPDX-License-Identifier: GPL-3.0-or-later
// ABI signatures adapted from DexNom/VIctory crates/victory-gpu/src/list.rs.

using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.Commands.Models;

internal readonly record struct DeferredCopy(
    ulong Destination,
    ulong Source,
    ulong Size,
    LinearTextureTransfer? Texture = null);
