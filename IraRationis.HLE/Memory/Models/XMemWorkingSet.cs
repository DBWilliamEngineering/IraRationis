// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx victory-hle/src/modules/xmem.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Memory.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct XMemWorkingSet
{
    public nuint TitleLimit;
    public nuint TitleUsed;
    public nuint ToolsLimit;
    public nuint ToolsUsed;
    public uint StackUsed;
    public uint PhysicalPages;
    public nuint GpuOptimalLimit;
    public nuint GpuOptimalUsed;
    public nuint ToolsGpuOptimalLimit;
    public nuint ToolsGpuOptimalUsed;
    public uint KernelPoolUsed;
    public uint KernelPoolLimit;
}
