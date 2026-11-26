// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace IraRationis.Shaders.Raster.Models;

public readonly record struct RasterNode(RasterExpression Operation, int Left = 0, int Right = 0,
    Vector4 Constant = default);
