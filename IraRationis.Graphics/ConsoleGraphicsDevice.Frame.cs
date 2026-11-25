// SPDX-License-Identifier: GPL-3.0-or-later
// Frame clock semantics derived from Zettrox/ztrx victory-gpu/src/frame.rs.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly Lock FrameGate = new();
    private static readonly Lock[] FrameEventGates = [new(), new(), new()];
    private static readonly long[] LastFrameTicks = new long[3];
    private static long _frameStart = Stopwatch.GetTimestamp();
    private static long _frameInterval = IntervalTicks(16_667);
    private static long _firstFrameTick;
    private static long _frameToken;
    private static int _reportedEventOffsets;

    private static long IntervalTicks(uint microseconds)
    {
        return Math.Max(1L, (long)Math.Round(microseconds * (Stopwatch.Frequency / 1_000_000.0)));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetFrameInterval(nint self, nint output, uint length, uint period, uint flags)
    {
        var interval = length == 0 ? 16_667U : length;
        lock (FrameGate)
        {
            _firstFrameTick = Math.Max(LastFrameTicks[0], Math.Max(LastFrameTicks[1], LastFrameTicks[2]));
            _frameStart = Stopwatch.GetTimestamp();
            _frameInterval = IntervalTicks(interval);
        }

        Console.Error.WriteLine($"[graphics] Frame interval {interval} us ({1_000_000.0 / interval:F2} Hz), period={period}, flags=0x{flags:X}.");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ScheduleFrameEvent(nint self, uint kind, uint offset, nint list, uint flags)
    {
        // Like upstream, all event kinds are scheduled at the tick; offsets are not yet modeled.
        if ((offset != 0 || list != 0 || flags != 0) && Interlocked.Exchange(ref _reportedEventOffsets, 1) == 0)
        {
            Console.Error.WriteLine($"[graphics] Frame event kind={kind}, offset={offset}, flags=0x{flags:X}; using clock-only scheduling.");
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int WaitFrameEvent(nint self, uint kind, uint timeoutMs, nint list, uint flags, ulong* token)
    {
        var index = (int)Math.Min(kind, 2U);
        var timeout = timeoutMs * (Stopwatch.Frequency / 1000.0);
        lock (FrameEventGates[index])
        {
            long tick;
            long delay;
            lock (FrameGate)
            {
                var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - _frameStart);
                var next = _firstFrameTick + elapsed / _frameInterval + 1;
                tick = Math.Max(next, LastFrameTicks[index] + 1);
                delay = Math.Max(0, (tick - _firstFrameTick) * _frameInterval - elapsed);
                if (timeout >= delay && tick > LastFrameTicks[index] + 1)
                {
                    // A late blocking wait can consume the tick which already passed.
                    tick--;
                    delay = 0;
                }
            }

            var timedOut = delay > timeout;
            var sleepTicks = timedOut ? (long)timeout : delay;
            SleepFrameTicks(sleepTicks);

            if (!timedOut)
            {
                lock (FrameGate)
                {
                    LastFrameTicks[index] = Math.Max(LastFrameTicks[index], tick);
                }
            }

            if (kind == 0 && (!timedOut || (flags & 2) != 0))
            {
                var frame = (ulong)Interlocked.Increment(ref _frameToken);
                if (token != null)
                {
                    *token = frame;
                }
            }

            return timedOut ? 1 : 0;
        }
    }

    private static void SleepFrameTicks(long ticks)
    {
        if (ticks <= 0)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + ticks;
        while (true)
        {
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var milliseconds = remaining * (1000.0 / Stopwatch.Frequency);
            if (milliseconds >= 2)
            {
                Thread.Sleep((int)Math.Min(int.MaxValue, Math.Floor(milliseconds - 1)));
            }
            else
            {
                // Only the final short interval is spun; no CPU-wide busy wait.
                Thread.SpinWait(32);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FrameStatistics(nint self, ulong token, uint kinds, uint* count, nint output)
    {
        // Upstream exposes no statistics yet; do not manufacture present timestamps.
        if (count != null)
        {
            *count = 0;
        }

        return 0;
    }

    private static void RegisterFrameMethods(nint* table)
    {
        table[78] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, int>)&SetFrameInterval;
        table[79] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, int>)&ScheduleFrameEvent;
        table[80] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, ulong*, int>)&WaitFrameEvent;
        table[81] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, uint*, nint, int>)&FrameStatistics;
    }
}
