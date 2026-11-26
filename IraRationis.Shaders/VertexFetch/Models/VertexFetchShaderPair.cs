// SPDX-License-Identifier: GPL-3.0-or-later
// GFX10 instruction encodings and P0 interpolation semantics cross-checked
// against LLVM AMDGPU and VIctory/victory-fork gfx10 and emit/lower.rs.


namespace IraRationis.Shaders.VertexFetch.Models;

public sealed record VertexFetchShaderPair(
    string VertexSource,
    string PixelSource,
    uint Register,
    uint Space,
    VertexFetchField Position,
    VertexFetchField Attribute);
