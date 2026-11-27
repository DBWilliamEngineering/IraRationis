// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct PreviewBitmapHeader
{
    public uint Size;
    public int Width, Height;
    public ushort Planes, Bits;
    public uint Compression, ImageBytes;
    public int XPerMeter, YPerMeter;
    public uint ColorsUsed, Important;
}
