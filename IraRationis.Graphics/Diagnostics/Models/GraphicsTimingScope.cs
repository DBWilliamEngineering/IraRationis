// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using GraphicsDevice = IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Diagnostics.Models;

internal readonly struct GraphicsTimingScope(GraphicsStage stage, long started, bool enabled, CpuSample cpuSample)
    : IDisposable
{
    public void Dispose()
    {
        if (!enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();

        GraphicsDevice.EndCpuSample(stage, cpuSample);

        GraphicsDevice.RecordTiming(stage, now - started);

        Interlocked.Decrement(ref GraphicsDevice.GraphicsTimingCounters[(int)stage].Active);

        if (stage == GraphicsStage.PresentX)
        {
            Interlocked.Exchange(ref GraphicsDevice._lastPresentExit, now);
        }
    }
}
