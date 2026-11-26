// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    private static readonly bool R24Verbose = Environment.GetEnvironmentVariable("IRX_VERBOSE_GRAPHICS") == "1";
    private static readonly ConcurrentDictionary<(string File, int Line), R46TraceCounter> R24TraceCounts = new();
    private static readonly object R24MetricsGate = new();
    private static Timer? _r46ReportTimer;
    private static long _r24FenceCreations, _r24FenceReuses, _r24FenceWaits, _r24FenceTicks;
    private static long _r24GuestWaits, _r24GuestWaitTicks, _r24PreviousGuestWaits, _r24PreviousGuestWaitTicks;
    private static long _r24Draws, _r24Dispatches, _r24PresentAttempts, _r24PresentSuccesses, _r24PresentTicks;
    private static long _r24LastReport = Stopwatch.GetTimestamp();
    private static long _r24PreviousWaits, _r24PreviousTicks, _r24PreviousDraws, _r24PreviousDispatches;
    private static long _r24PreviousAttempts, _r24PreviousSuccesses, _r24PreviousPresentTicks;

    // Only repetitive success messages use this method. Failures, first-use
    // markers and correctness evidence retain their original logging paths.
    private static void TraceR24(string message, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (R24Verbose)
        {
            Console.Error.WriteLine(message);

            return;
        }

        var counter = R24TraceCounts.GetOrAdd((file, line), static _ => new R46TraceCounter());
        var count = Interlocked.Increment(ref counter.Count);

        if (count <= 4 || (count & (count - 1)) == 0)
        {
            Console.Error.WriteLine(message);
        }
    }

    private static void RecordR24FenceWait(long ticks)
    {
        Interlocked.Add(ref _r24FenceTicks, ticks);

        Interlocked.Increment(ref _r24FenceWaits);

        ReportR24Metrics();
    }

    private static void RecordR24GuestWait(long ticks)
    {
        Interlocked.Add(ref _r24GuestWaitTicks, ticks);

        Interlocked.Increment(ref _r24GuestWaits);

        ReportR24Metrics();
    }

    private static void RecordR24Present(int hr, long ticks)
    {
        Interlocked.Add(ref _r24PresentTicks, ticks);

        Interlocked.Increment(ref _r24PresentAttempts);

        if (hr == 0)
        {
            Interlocked.Increment(ref _r24PresentSuccesses);
        }

        ReportR24Metrics();
    }

    private static void ReportR24Metrics()
    {
        if (Volatile.Read(ref _r46ReportTimer) is not null)
        {
            return;
        }

        lock (R24MetricsGate)
        {
            if (_r46ReportTimer is null)
            {
                _r24LastReport = Stopwatch.GetTimestamp();

                Volatile.Write(ref _r46ReportTimer, new Timer(static _ => ReportR46Metrics(), null, 2000, 2000));
            }
        }
    }

    private static void ReportR46Metrics()
    {
        var now = Stopwatch.GetTimestamp();

        if (now - Volatile.Read(ref _r24LastReport) < 2 * Stopwatch.Frequency || !Monitor.TryEnter(R24MetricsGate))
        {
            return;
        }

        try
        {
            now = Stopwatch.GetTimestamp();

            var elapsed = (now - _r24LastReport) / (double)Stopwatch.Frequency;

            if (elapsed < 2)
            {
                return;
            }

            var waits = Interlocked.Read(ref _r24FenceWaits);
            var ticks = Interlocked.Read(ref _r24FenceTicks);
            var guestWaits = Interlocked.Read(ref _r24GuestWaits);
            var guestTicks = Interlocked.Read(ref _r24GuestWaitTicks);
            var guestWaitMs = (guestTicks - _r24PreviousGuestWaitTicks) * 1000.0 / Stopwatch.Frequency;
            var draws = Interlocked.Read(ref _r24Draws);
            var dispatches = Interlocked.Read(ref _r24Dispatches);
            var attempts = Interlocked.Read(ref _r24PresentAttempts);
            var successes = Interlocked.Read(ref _r24PresentSuccesses);
            var presentTicks = Interlocked.Read(ref _r24PresentTicks);
            var waitDelta = waits - _r24PreviousWaits;
            var attemptDelta = attempts - _r24PreviousAttempts;
            var waitMs = (ticks - _r24PreviousTicks) * 1000.0 / Stopwatch.Frequency;
            var presentMs = (presentTicks - _r24PreviousPresentTicks) * 1000.0 / Stopwatch.Frequency;

            Console.Error.WriteLine(FormattableString.Invariant(
                $"[performance] IRX-R24: interval={elapsed:F2}s; presentRate={(successes - _r24PreviousSuccesses) / elapsed:F2}/s; presents={successes}; attempts={attemptDelta}; draws={draws - _r24PreviousDraws}; dispatches={dispatches - _r24PreviousDispatches}; fenceWaits={waitDelta}; fenceMsTotal={waitMs:F1}; fenceMsAvg={(waitDelta == 0 ? 0 : waitMs / waitDelta):F3}; guestWaits={guestWaits - _r24PreviousGuestWaits}; guestWaitMsTotal={guestWaitMs:F1}; presentMsAvg={(attemptDelta == 0 ? 0 : presentMs / attemptDelta):F3}; fencesCreated={Interlocked.Read(ref _r24FenceCreations)}; fenceReuses={Interlocked.Read(ref _r24FenceReuses)}."));

            _r24PreviousGuestWaits = guestWaits;

            _r24PreviousGuestWaitTicks = guestTicks;

            _r24PreviousWaits = waits;

            _r24PreviousTicks = ticks;

            _r24PreviousDraws = draws;

            _r24PreviousDispatches = dispatches;

            _r24PreviousAttempts = attempts;

            _r24PreviousSuccesses = successes;

            _r24PreviousPresentTicks = presentTicks;

            Volatile.Write(ref _r24LastReport, now);
        }
        catch (Exception)
        {
            // A closed diagnostic stream must not terminate execution.
        }
        finally
        {
            Monitor.Exit(R24MetricsGate);
        }
    }

    private sealed class R46TraceCounter
    {
        public long Count;
    }
}
