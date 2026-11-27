// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Raster.Models;

public sealed record RectangleShaderPair(
    string VertexSource,
    string GeometrySource,
    string PixelSource,
    uint Register,
    uint Space,
    bool IndexedArray = false,
    bool SamplesDualMasks = false)
{
    public int IndexedVertexConstantBytes { get; init; } = 64;
}
