// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct VideoBitmapHeader
{
    public uint Size;
    public int Width, Height;
    public ushort Planes, BitCount;
    public uint Compression, ImageSize;
    public int XPels, YPels;
    public uint ColorsUsed, Important;
}
