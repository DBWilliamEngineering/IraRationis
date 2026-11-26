// SPDX-License-Identifier: GPL-3.0-or-later
// Layout and addressing adapted from Zettrox/ztrx victory-formats/src/texture.rs.
// AMD addrlib attribution: AMD-ADDRLIB-NOTICE.txt.


namespace IraRationis.Graphics.Textures.Models;

internal readonly record struct TextureMip(
    uint Width,
    uint Height,
    uint Depth,
    uint Pitch,
    uint Rows,
    uint DepthPitch,
    ulong Offset = 0,
    bool InTail = false,
    uint TailOffset = 0,
    uint TailX = 0,
    uint TailY = 0);
