// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Shaders.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipelineObject
{
    public GraphicsObjectHeader Header;
    public fixed uint Packet[8];
}
