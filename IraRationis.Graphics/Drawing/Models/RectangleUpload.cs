// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Drawing.Models;

internal sealed class RectangleUpload(ulong table, ulong vertex, uint stride, nint host)
{
    public readonly nint Host = host;
    public readonly uint Stride = stride;
    public readonly ulong Table = table;
    public readonly ulong Vertex = vertex;
    public int Capacity;
    public int ConstantBytes = ConsoleGraphicsDevice.RectangleConstantBytes;
    public int ConstantOffset;
    public IndexedUploadPlan? Indexed;
    public bool IndexedArray;
    public bool IndexedColor;
    public int Phase;
    public nint Target;
}
