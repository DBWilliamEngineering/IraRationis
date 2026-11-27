// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx victory-hle/src/modules/xmem.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Memory.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct XMemProcessMemoryCounters
{
    public uint Size;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
}
