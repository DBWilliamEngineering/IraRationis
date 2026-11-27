// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI derived from Zettrox/ztrx victory-gpu: device, object, queue, fence, list and resource.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Device.Models;

[StructLayout(LayoutKind.Sequential)]
public struct GraphicsListDescription
{
    public uint Kind;
    public uint Flags;
    public uint Nodes;
    public uint BarrierBatch;
}
