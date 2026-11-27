// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Drawing.Models;

[StructLayout(LayoutKind.Sequential)]
public readonly struct RasterVertex
{
    public readonly Vector4 Position, Color, TexCoord;

    public RasterVertex(Vector4 position, Vector4 color, Vector4 texCoord)
    {
        Position = position;
        Color = color;
        TexCoord = texCoord;
    }
}
