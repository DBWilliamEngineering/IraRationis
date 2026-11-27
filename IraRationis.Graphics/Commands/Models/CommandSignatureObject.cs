// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI adapted from Zettrox/ztrx victory-gpu/src/signature.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Commands.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct CommandSignatureObject
{
    public GraphicsObjectHeader Header;
    public uint Stride;
    public uint Count;
    public uint Nodes;
    public nint Root;
}
