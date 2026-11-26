// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics.Models;

internal sealed class GraphicsTimingCounter
{
    public long Active;
    public long Calls;
    public long Maximum;
    public long PreviousCalls;
    public long PreviousTicks;
    public long Slow;
    public long Ticks;
}
