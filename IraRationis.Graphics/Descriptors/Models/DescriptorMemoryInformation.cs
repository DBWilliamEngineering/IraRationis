// SPDX-License-Identifier: GPL-3.0-or-later
// Console descriptor ABI adapted from Zettrox/ztrx crates/victory-gpu/src/descriptor.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Descriptors.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DescriptorMemoryInformation
{
    public nuint BaseAddress;
    public nuint AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}
