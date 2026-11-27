// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.


namespace IraRationis.Graphics.Shaders.Models;

// Only pointer-free state records are retained. Input elements have their own managed deep copy.
internal sealed unsafe record PipelineStateRecord(uint Kind, byte[] Bytes);
