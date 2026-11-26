// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Shaders.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipelineStreamDescription
{
    public nuint Size;
    public byte* Stream;
}
