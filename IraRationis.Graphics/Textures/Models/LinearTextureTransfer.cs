// SPDX-License-Identifier: GPL-3.0-or-later
// Independent linear copy implementation using documented Windows D3D12 contracts.


namespace IraRationis.Graphics.Textures.Models;

internal sealed record LinearTextureTransfer(
    LinearCopyLocation Source,
    LinearCopyLocation Destination,
    LinearSurface SourceSurface,
    LinearSurface DestinationSurface,
    LinearCopyBox Region,
    uint X,
    uint Y,
    LinearCopyPlan Plan);
