// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Textures.Models;

internal sealed record HostTextureCopy(
    TextureLayout Layout,
    ulong Bytes,
    uint[] Pitches,
    ulong[] Offsets,
    ulong[][] Columns);
