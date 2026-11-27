// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IraRationis.Graphics.Diagnostics.Models;

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    internal static readonly bool EnableVerboseGraphics =
        Environment.GetEnvironmentVariable("IRX_VERBOSE_GRAPHICS") == "1";
    private static readonly ConcurrentDictionary<(string File, int Line), GraphicsTraceCounter> GraphicsTraceCounts =
        new();
    private static readonly object RuntimeMetricsGate = new();
    private static Timer? _runtimeMetricsTimer;
    internal static long _fenceCreations, _fenceReuses, _fenceWaits, _fenceTicks;
    private static long _guestWaits, _guestWaitTicks, _previousGuestWaits, _previousGuestWaitTicks;
    private static long _draws, _dispatches, _presentAttempts, _presentSuccesses, _presentTicks;
    private static long _lastRuntimeMetricsReport = Stopwatch.GetTimestamp();
    private static long _previousWaits, _previousTicks, _previousDraws, _previousDispatches;
    private static long _previousAttempts, _previousSuccesses, _previousPresentTicks;

    private static readonly bool EnableCommandListSampling =
        Environment.GetEnvironmentVariable("IRX_SOURCE_LIST_SAMPLING") == "1";
    private static readonly long[] CommandListCreationCounts = new long[8];

    private static void TraceListCreation(uint kind)
    {
        if (!EnableCommandListSampling || EnableVerboseGraphics || kind >= (uint)CommandListCreationCounts.Length)
        {
            Console.Error.WriteLine($"[graphics] Command list type {kind} created.");

            return;
        }

        var count = Interlocked.Increment(ref CommandListCreationCounts[(int)kind]);

        if (count <= 4 || (count & (count - 1)) == 0)
        {
            Console.Error.WriteLine($"[graphics] IRX-R69-LIST: kind={kind}; created={count}.");
        }
    }

    // Only repetitive success messages use this method. Failures, first-use
    // markers and correctness evidence retain their original logging paths.
    private static void TraceGraphicsSuccess(string message, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        if (EnableVerboseGraphics)
        {
            Console.Error.WriteLine(message);

            return;
        }

        var counter = GraphicsTraceCounts.GetOrAdd((file, line), static _ => new GraphicsTraceCounter());
        var count = Interlocked.Increment(ref counter.Count);

        if (count <= 4 || (count & (count - 1)) == 0)
        {
            Console.Error.WriteLine(message);
        }
    }

    internal static void RecordFenceWait(long ticks)
    {
        Interlocked.Add(ref _fenceTicks, ticks);

        Interlocked.Increment(ref _fenceWaits);

        EnsureRuntimeMetricsTimer();
    }

    private static void RecordGuestWait(long ticks)
    {
        Interlocked.Add(ref _guestWaitTicks, ticks);

        Interlocked.Increment(ref _guestWaits);

        EnsureRuntimeMetricsTimer();
    }

    private static void RecordPresent(int resultCode, long ticks)
    {
        Interlocked.Add(ref _presentTicks, ticks);

        Interlocked.Increment(ref _presentAttempts);

        if (resultCode == 0)
        {
            Interlocked.Increment(ref _presentSuccesses);
        }

        EnsureRuntimeMetricsTimer();
    }

    private static void EnsureRuntimeMetricsTimer()
    {
        if (Volatile.Read(ref _runtimeMetricsTimer) is not null)
        {
            return;
        }

        lock (RuntimeMetricsGate)
        {
            if (_runtimeMetricsTimer is null)
            {
                _lastRuntimeMetricsReport = Stopwatch.GetTimestamp();

                Volatile.Write(ref _runtimeMetricsTimer,
                    new Timer(static _ => ReportRuntimeMetrics(), null, 2000, 2000));
            }
        }
    }

    private static void ReportRuntimeMetrics()
    {
        var now = Stopwatch.GetTimestamp();

        if (now - Volatile.Read(ref _lastRuntimeMetricsReport) < 2 * Stopwatch.Frequency
            || !Monitor.TryEnter(RuntimeMetricsGate))
        {
            return;
        }

        try
        {
            now = Stopwatch.GetTimestamp();

            var elapsed = (now - _lastRuntimeMetricsReport) / (double)Stopwatch.Frequency;

            if (elapsed < 2)
            {
                return;
            }

            var waits = Interlocked.Read(ref _fenceWaits);
            var ticks = Interlocked.Read(ref _fenceTicks);
            var guestWaits = Interlocked.Read(ref _guestWaits);
            var guestTicks = Interlocked.Read(ref _guestWaitTicks);
            var guestWaitMs = (guestTicks - _previousGuestWaitTicks) * 1000.0 / Stopwatch.Frequency;
            var draws = Interlocked.Read(ref _draws);
            var dispatches = Interlocked.Read(ref _dispatches);
            var attempts = Interlocked.Read(ref _presentAttempts);
            var successes = Interlocked.Read(ref _presentSuccesses);
            var presentTicks = Interlocked.Read(ref _presentTicks);
            var waitDelta = waits - _previousWaits;
            var attemptDelta = attempts - _previousAttempts;
            var waitMs = (ticks - _previousTicks) * 1000.0 / Stopwatch.Frequency;
            var presentMs = (presentTicks - _previousPresentTicks) * 1000.0 / Stopwatch.Frequency;

            Console.Error.WriteLine(FormattableString.Invariant(
                $"[performance] IRX-R24: interval={elapsed:F2}s; presentRate={(successes - _previousSuccesses) / elapsed:F2}/s; presents={successes}; attempts={attemptDelta}; draws={draws - _previousDraws}; dispatches={dispatches - _previousDispatches}; fenceWaits={waitDelta}; fenceMsTotal={waitMs:F1}; fenceMsAvg={(waitDelta == 0 ? 0 : waitMs / waitDelta):F3}; guestWaits={guestWaits - _previousGuestWaits}; guestWaitMsTotal={guestWaitMs:F1}; presentMsAvg={(attemptDelta == 0 ? 0 : presentMs / attemptDelta):F3}; fencesCreated={Interlocked.Read(ref _fenceCreations)}; fenceReuses={Interlocked.Read(ref _fenceReuses)}."));

            _previousGuestWaits = guestWaits;

            _previousGuestWaitTicks = guestTicks;

            _previousWaits = waits;

            _previousTicks = ticks;

            _previousDraws = draws;

            _previousDispatches = dispatches;

            _previousAttempts = attempts;

            _previousSuccesses = successes;

            _previousPresentTicks = presentTicks;

            Volatile.Write(ref _lastRuntimeMetricsReport, now);
        }
        catch (Exception)
        {
            // A closed diagnostic stream must not terminate execution.
        }
        finally
        {
            Monitor.Exit(RuntimeMetricsGate);
        }
    }
}
