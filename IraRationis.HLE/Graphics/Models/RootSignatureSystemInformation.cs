// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-gpu/src/root.rs serialization entry points.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Graphics.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RootSignatureSystemInformation
{
    public uint ProcessorArchitecture;
    public uint PageSize;
    public nuint MinimumApplicationAddress;
    public nuint MaximumApplicationAddress;
    public nuint ActiveProcessorMask;
    public uint NumberOfProcessors;
    public uint ProcessorType;
    public uint AllocationGranularity;
    public ushort ProcessorLevel;
    public ushort ProcessorRevision;
}
