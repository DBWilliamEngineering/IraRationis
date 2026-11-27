// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.


namespace IraRationis.Graphics.Shaders.Models;

internal sealed unsafe class ComputeShaderContainer(byte[] bytes)
{
    public readonly byte[] Bytes = bytes;
    public int Inspected;
    public int References;
}
