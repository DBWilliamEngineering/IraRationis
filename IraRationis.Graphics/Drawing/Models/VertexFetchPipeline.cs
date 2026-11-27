// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Shaders.VertexFetch.Models;

namespace IraRationis.Graphics.Drawing.Models;

internal sealed record VertexFetchPipeline(
    VertexFetchShaderPair Pair,
    nint Root,
    nint Pipeline,
    uint ColorFormat,
    bool DepthBound,
    bool DepthBoundsEnabled);
