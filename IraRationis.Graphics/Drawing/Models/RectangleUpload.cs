// SPDX-License-Identifier: GPL-3.0-or-later

using GraphicsDevice = global::IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Drawing.Models;

internal sealed unsafe class RectangleUpload(ulong table, ulong vertex, uint stride, nint host)
{
    public readonly nint Host = host;
    public readonly uint Stride = stride;
    public readonly ulong Table = table;
    public readonly ulong Vertex = vertex;
    public int Capacity;
    public int ConstantBytes = GraphicsDevice.RectangleConstantBytes;
    public int ConstantOffset;
    public int NonIndexedVertexBytes;
    public IndexedUploadPlan? Indexed;
    public bool IndexedArray;
    public bool IndexedColor;
    public DualMaskRectangleBindings? DualMask;
    public int Phase;
    public nint Target;
}
