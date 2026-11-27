// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics.Models;

internal sealed class CpuCounter
{
    public long Calls;
    public long Completed;
    public long Failed;
    public long CpuTicks;
    public long WallTicks;
    public uint LastSampleThreadId;
}
