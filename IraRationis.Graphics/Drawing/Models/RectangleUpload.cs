// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands.Models;
using GraphicsDevice = IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Drawing.Models;

internal sealed class RectangleUpload(ulong table, ulong vertex, uint stride, RecordedUploadSlice slice)
{
    public readonly RecordedUploadSlice Slice = slice;
    public readonly uint Stride = stride;
    public readonly ulong Table = table;
    public readonly ulong Vertex = vertex;
    public bool AlphaOnlyTarget;
    public int Capacity;
    public int ConstantBytes = GraphicsDevice.RectangleConstantBytes;
    public int ConstantOffset;
    public DualMaskRectangleBindings? DualMask;
    public IndexedUploadPlan? Indexed;
    public bool IndexedArray;
    public bool IndexedColor;
    public int NonIndexedVertexBytes;
    public int Phase;
    public nint Target;
}
