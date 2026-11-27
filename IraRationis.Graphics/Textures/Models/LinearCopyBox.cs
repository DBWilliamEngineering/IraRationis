// SPDX-License-Identifier: GPL-3.0-or-later
// Independent linear copy implementation using documented Windows D3D12 contracts.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Textures.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LinearCopyBox
{
    public uint Left;
    public uint Top;
    public uint Front;
    public uint Right;
    public uint Bottom;
    public uint Back;
}
