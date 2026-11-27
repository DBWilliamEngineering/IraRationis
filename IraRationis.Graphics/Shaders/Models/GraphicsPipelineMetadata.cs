// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.

using IraRationis.Graphics.Drawing.Models;

namespace IraRationis.Graphics.Shaders.Models;

internal sealed unsafe record GraphicsPipelineMetadata(
    nint Root,
    List<PipelineShader> Shaders,
    List<PipelineStateRecord> State,
    bool IsStream,
    bool CompleteStream,
    string? IncompleteReason = null,
    VertexInputElement[]? InputLayout = null);
