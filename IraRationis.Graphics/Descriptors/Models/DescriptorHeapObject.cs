// SPDX-License-Identifier: GPL-3.0-or-later
// Console descriptor ABI adapted from Zettrox/ztrx crates/victory-gpu/src/descriptor.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Descriptors.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DescriptorHeapObject
{
    public GraphicsObjectHeader Header;
    public DescriptorHeapDescription Description;
    public ulong Start;
    public nint OwnedMemory;
}
