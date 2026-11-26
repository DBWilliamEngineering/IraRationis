// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace IraRationis.Graphics.Diagnostics.Models;

internal readonly struct GraphicsTimingScope(GraphicsStage stage, long started, bool enabled, CpuSample cpuSample) : IDisposable
{
    public void Dispose()
    {
        if (!enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        ConsoleGraphicsDevice.EndCpuSample(stage, cpuSample);

        ConsoleGraphicsDevice.RecordTiming(stage, now - started);

        Interlocked.Decrement(ref ConsoleGraphicsDevice.GraphicsTimingCounters[(int)stage].Active);

        if (stage == GraphicsStage.PresentX)
        {
            Interlocked.Exchange(ref ConsoleGraphicsDevice._lastPresentExit, now);
        }
    }
}
