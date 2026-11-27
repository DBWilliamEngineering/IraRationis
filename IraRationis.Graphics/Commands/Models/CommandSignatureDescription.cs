// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI adapted from Zettrox/ztrx victory-gpu/src/signature.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Commands.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct CommandSignatureDescription
{
    public uint Stride;
    public uint Count;
    public uint* Arguments;
    public uint Nodes;
}
