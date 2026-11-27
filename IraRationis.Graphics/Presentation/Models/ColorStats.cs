// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Presentation.Models;

internal readonly unsafe record struct ColorStats(
    int NonBlack,
    int NonZeroAlpha,
    byte MaxRgb,
    int Left,
    int Top,
    int Right,
    int Bottom,
    uint Center);
