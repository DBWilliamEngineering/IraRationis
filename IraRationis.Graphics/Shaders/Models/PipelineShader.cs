// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.


namespace IraRationis.Graphics.Shaders.Models;

internal sealed record PipelineShader(uint Stage, string Hash, ComputeShaderContainer Container);
