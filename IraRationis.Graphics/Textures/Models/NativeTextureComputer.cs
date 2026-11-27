// SPDX-License-Identifier: GPL-3.0-or-later
// XG ABI adapted from Zettrox/ztrx victory-hle/src/modules/xg.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Textures.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTextureComputer
{
    public nint Table;
    public int References;
    public nint Handle;
}
