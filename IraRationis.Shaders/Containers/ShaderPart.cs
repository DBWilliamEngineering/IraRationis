// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-shader/src/dxbc.rs and dxil.rs, Zettrox/ztrx.

namespace IraRationis.Shaders.Containers;

public sealed record ShaderPart(string FourCC, uint Offset, ReadOnlyMemory<byte> Data);
