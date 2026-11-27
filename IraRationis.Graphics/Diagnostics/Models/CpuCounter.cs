// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics.Models;

internal sealed class CpuCounter
{
    public long Calls;
    public long Completed;
    public long CpuTicks;
    public long Failed;
    public uint LastSampleThreadId;
    public long WallTicks;
}
