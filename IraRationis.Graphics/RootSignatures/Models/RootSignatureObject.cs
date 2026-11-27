// SPDX-License-Identifier: GPL-3.0-or-later
// Console object ABI adapted from Zettrox/ztrx victory-gpu/src/root.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.RootSignatures.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct RootSignatureObject
{
    public GraphicsObjectHeader Header;
    public nuint BlobSize;
}
