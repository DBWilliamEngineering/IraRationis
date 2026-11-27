// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace IraRationis.HLE.Audio.Movie;

internal sealed class MovieMixerWaitAudit
{
    internal const uint MaximumTimedWaitMilliseconds = 16;
    private static readonly long MixerQuantumTicks = (Stopwatch.Frequency * 256 + 47_999) / 48_000;
    private long _yields;
    private long _waits;
    private long _requestedMilliseconds;
    private long _elapsedTicks;
    private long _lateByQuantum;
    private long _maximumOvershootTicks;
    private long _preciseWaits;
    private long _preciseFallbacks;
    private long _reportedYields;
    private long _reportedWaits;

    internal void RecordYield()
    {
        Interlocked.Increment(ref _yields);
    }

    internal void RecordWait(uint requestedMilliseconds, long elapsedTicks, bool triedPrecise, bool usedPrecise)
    {
        var overshoot = elapsedTicks - requestedMilliseconds * Stopwatch.Frequency / 1_000;

        Interlocked.Add(ref _requestedMilliseconds, requestedMilliseconds);
        Interlocked.Add(ref _elapsedTicks, elapsedTicks);

        if (triedPrecise)
        {
            if (usedPrecise)
            {
                Interlocked.Increment(ref _preciseWaits);
            }
            else
            {
                Interlocked.Increment(ref _preciseFallbacks);
            }
        }

        if (overshoot >= MixerQuantumTicks)
        {
            Interlocked.Increment(ref _lateByQuantum);
        }

        for (var maximum = Volatile.Read(ref _maximumOvershootTicks); overshoot > maximum;
             maximum = Volatile.Read(ref _maximumOvershootTicks))
        {
            if (Interlocked.CompareExchange(ref _maximumOvershootTicks, overshoot, maximum) == maximum)
            {
                break;
            }
        }

        Interlocked.Increment(ref _waits);
    }

    internal string? TakeReport()
    {
        var yields = Interlocked.Read(ref _yields);
        var waits = Interlocked.Read(ref _waits);

        if (yields == _reportedYields && waits == _reportedWaits)
        {
            return null;
        }

        _reportedYields = yields;
        _reportedWaits = waits;

        var elapsedMilliseconds = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _elapsedTicks)).TotalMilliseconds;
        var maximumOvershoot = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _maximumOvershootTicks)).TotalMilliseconds;

        return string.Create(CultureInfo.InvariantCulture,
            $"mixerYields={yields}; mixerTimedWaits={waits}; mixerRequestedMs={Interlocked.Read(ref _requestedMilliseconds)}; " +
            $"mixerElapsedMs={elapsedMilliseconds:F2}; mixerLateByQuantum={Interlocked.Read(ref _lateByQuantum)}; " +
            $"mixerMaxOvershootMs={maximumOvershoot:F2}; mixerPreciseWaits={Interlocked.Read(ref _preciseWaits)}; " +
            $"mixerPreciseFallbacks={Interlocked.Read(ref _preciseFallbacks)}");
    }
}
