// SPDX-License-Identifier: GPL-3.0-or-later
// Layout and addressing adapted from Zettrox/ztrx victory-formats/src/texture.rs.
// AMD addrlib attribution: AMD-ADDRLIB-NOTICE.txt.

using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.Textures;

internal sealed record TextureLayout(
    ResourceDescription Description,
    TexturePlane[] Planes,
    ulong Size,
    ulong Alignment);
