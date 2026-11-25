// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Text;

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    private static readonly bool EnableR25Timing = Environment.GetEnvironmentVariable("IRX_PROFILE_TIMING") != "0";
    private static readonly R25Counter[] R25Counters = CreateR25Counters();
    private static readonly object R25ReportGate = new();
    private static Timer? _r25Timer;
    private static int _r25Started;
    private static long _r25LastReport;
    private static long _r25PreviousCpuTicks;
    private static long _r25LastPresentExit;

    private static R25Counter[] CreateR25Counters()
    {
        var counters = new R25Counter[(int)R25Stage.Count];
        for (var i = 0; i < counters.Length; i++)
        {
            counters[i] = new R25Counter();
        }

        return counters;
    }

    private static R25Timing BeginR25Timing(R25Stage stage)
    {
        if (!EnableR25Timing)
        {
            return default;
        }

        if (Interlocked.CompareExchange(ref _r25Started, 1, 0) == 0)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                _r25PreviousCpuTicks = process.TotalProcessorTime.Ticks;
                _r25LastReport = Stopwatch.GetTimestamp();
                _r25Timer = new Timer(static _ => ReportR25Timing(), null, 2000, 2000);
                LogR25("[performance] IRX-R25: timing active; inclusive thread-wall timings, not additive; period=2s.");
            }
            catch (Exception exception)
            {
                LogR25($"[performance] IRX-R25: periodic reporting unavailable: {exception.Message}");
            }
        }

        var now = Stopwatch.GetTimestamp();
        if (stage == R25Stage.PresentX)
        {
            var previous = Interlocked.Exchange(ref _r25LastPresentExit, 0);
            if (previous != 0 && now >= previous)
            {
                RecordR25Timing(R25Stage.PresentGap, now - previous);
            }
        }

        Interlocked.Increment(ref R25Counters[(int)stage].Active);

        return new R25Timing(stage, now, true);
    }

    private static void RecordR25Timing(R25Stage stage, long ticks)
    {
        var counter = R25Counters[(int)stage];
        Interlocked.Add(ref counter.Ticks, ticks);
        Interlocked.Increment(ref counter.Calls);
        var maximum = Volatile.Read(ref counter.Maximum);
        while (ticks > maximum)
        {
            var previous = Interlocked.CompareExchange(ref counter.Maximum, ticks, maximum);
            if (previous == maximum)
            {
                break;
            }

            maximum = previous;
        }

        // Report unusually long completed operations, but never spam per frame.
        // PresentGap is expected to be long in R24 and is reported in the heartbeat.
        if (stage != R25Stage.PresentGap && ticks >= Stopwatch.Frequency / 10)
        {
            var slow = Interlocked.Increment(ref counter.Slow);
            if (slow <= 4 || (slow & (slow - 1)) == 0)
            {
                LogR25(FormattableString.Invariant(
                    $"[performance] IRX-R25-SLOW: stage={stage}; ms={ticks * 1000.0 / Stopwatch.Frequency:F3}; thread={Environment.CurrentManagedThreadId}; occurrences={slow}."));
            }
        }
    }

    private static void LogR25(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (Exception)
        {
            // Diagnostics must not affect guest execution when output closes.
        }
    }

    private static void ReportR25Timing()
    {
        if (!Monitor.TryEnter(R25ReportGate))
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _r25LastReport) / (double)Stopwatch.Frequency;
            if (elapsed < 1)
            {
                return;
            }

            using var process = Process.GetCurrentProcess();
            var cpuTicks = process.TotalProcessorTime.Ticks;
            var cpuCores = (cpuTicks - _r25PreviousCpuTicks) / (double)TimeSpan.TicksPerSecond / elapsed;
            var report = new StringBuilder(FormattableString.Invariant(
                $"[performance] IRX-R25: interval={elapsed:F2}s; cpuCoreEquiv={cpuCores:F2}; privateMiB={process.PrivateMemorySize64 / 1048576.0:F0}; workingSetMiB={process.WorkingSet64 / 1048576.0:F0}; gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)};"));
            for (var i = 0; i < R25Counters.Length; i++)
            {
                var counter = R25Counters[i];
                var calls = Interlocked.Read(ref counter.Calls);
                var ticks = Interlocked.Read(ref counter.Ticks);
                var maximum = Interlocked.Exchange(ref counter.Maximum, 0);
                var active = Interlocked.Read(ref counter.Active);
                var count = calls - counter.PreviousCalls;
                if (count != 0 || active != 0)
                {
                    report.Append(FormattableString.Invariant(
                        $" {(R25Stage)i}[n={count},ms={(ticks - counter.PreviousTicks) * 1000.0 / Stopwatch.Frequency:F2},max={maximum * 1000.0 / Stopwatch.Frequency:F2},active={active}];"));
                }

                counter.PreviousCalls = calls;
                counter.PreviousTicks = ticks;
            }

            _r25PreviousCpuTicks = cpuTicks;
            _r25LastReport = now;
            LogR25(report.ToString());
        }
        catch (Exception exception)
        {
            // Diagnostic reporting must never terminate the title.
            LogR25($"[performance] IRX-R25: report failed: {exception.Message}");
        }
        finally
        {
            Monitor.Exit(R25ReportGate);
        }
    }

    private enum R25Stage
    {
        PresentX,
        SwapChain,
        WindowSetup,
        SegmentedSubmit,
        SealBoundary,
        PublishWrite,
        GuestWait,
        RecordR13,
        ExecuteR13,
        RecordRectangle,
        ExecuteRectangle,
        RecordTexture,
        ExecuteTexture,
        RecordCompute,
        ExecuteCompute,
        UploadTexture,
        UtilitySubmit,
        PresentGap,
        Count
    }

    private sealed class R25Counter
    {
        public long Active;
        public long Calls;
        public long Maximum;
        public long PreviousCalls;
        public long PreviousTicks;
        public long Slow;
        public long Ticks;
    }

    private readonly struct R25Timing(R25Stage stage, long started, bool enabled) : IDisposable
    {
        public void Dispose()
        {
            if (!enabled)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            RecordR25Timing(stage, now - started);
            Interlocked.Decrement(ref R25Counters[(int)stage].Active);
            if (stage == R25Stage.PresentX)
            {
                Interlocked.Exchange(ref _r25LastPresentExit, now);
            }
        }
    }
}
