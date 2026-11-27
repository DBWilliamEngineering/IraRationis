// SPDX-License-Identifier: GPL-3.0-or-later
// Console descriptor ABI adapted from Zettrox/ztrx crates/victory-gpu/src/descriptor.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Descriptors.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct ConstantDescriptor
{
    public uint Tag;
    public uint Size;
    public ulong Address;
}
