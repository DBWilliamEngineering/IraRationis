// SPDX-License-Identifier: GPL-3.0-or-later
// Independent linear copy implementation using documented Windows D3D12 contracts.


namespace IraRationis.Graphics.Textures.Models;

internal readonly unsafe record struct LinearSurface(ulong Address, uint Width, uint Height, uint Format,
    uint PixelBytes, ulong Pitch, long Generation);
