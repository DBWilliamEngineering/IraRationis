// SPDX-License-Identifier: GPL-3.0-or-later
// Console DXGI object layouts derived from Zettrox/ztrx victory-gpu/src/dxgi.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayMode
{
    public uint Width;
    public uint Height;
    public uint RefreshNumerator;
    public uint RefreshDenominator;
    public uint Format;
    public uint Scanlines;
    public uint Scaling;
    public uint Flags;
}
