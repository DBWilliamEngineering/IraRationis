// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
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
    private static readonly bool EnableR27ThreadMetrics =
        Environment.GetEnvironmentVariable("IRX_PROFILE_THREADS") != "0";
    private static readonly Dictionary<int, (long StartTicks, long CpuTicks)> R27PreviousThreads = new();
    private static long _r27PreviousSample;

    // Called only by the existing report timer under R25ReportGate. This is a
    // sampled view of OS-thread CPU time, not a stack profiler or wait trace.
    private static void ReportR27Threads(Process process, long now)
    {
        if (!EnableR27ThreadMetrics)
        {
            return;
        }

        try
        {
            var previous = _r27PreviousSample;
            var elapsed = previous == 0 ? 0 : (now - previous) / (double)Stopwatch.Frequency;
            var current = new Dictionary<int, (long StartTicks, long CpuTicks)>();
            var hot = new List<(int Id, double Cores, string State)>();
            var threads = process.Threads;
            var missed = 0;

            foreach (ProcessThread thread in threads)
            {
                try
                {
                    var id = thread.Id;
                    var start = thread.StartTime.Ticks;
                    var cpu = thread.TotalProcessorTime.Ticks;

                    current[id] = (start, cpu);

                    if (elapsed > 0 && R27PreviousThreads.TryGetValue(id, out var old) &&
                        old.StartTicks == start && cpu >= old.CpuTicks)
                    {
                        var cores = (cpu - old.CpuTicks) / (double)TimeSpan.TicksPerSecond / elapsed;

                        if (cores >= 0.005)
                        {
                            hot.Add((id, cores, thread.ThreadState.ToString()));
                        }
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                      Win32Exception or NotSupportedException)
                {
                    // A thread can exit between enumeration and querying its times.
                    missed++;
                }
                finally
                {
                    thread.Dispose();
                }
            }

            R27PreviousThreads.Clear();

            foreach (var pair in current)
            {
                R27PreviousThreads.Add(pair.Key, pair.Value);
            }

            _r27PreviousSample = now;

            hot.Sort(static (left, right) => right.Cores.CompareTo(left.Cores));

            var report = new StringBuilder(FormattableString.Invariant(
                $"[performance] IRX-R27-THREADS: interval={elapsed:F2}s; sampled={current.Count}; missed={missed};"));

            for (var i = 0; i < Math.Min(5, hot.Count); i++)
            {
                var item = hot[i];

                report.Append(FormattableString.Invariant(
                    $" tid={item.Id}(0x{item.Id:X}),cores={item.Cores:F3},state={item.State};"));
            }

            LogR25(report.ToString());
        }
        catch (Exception exception)
        {
            LogR25($"[performance] IRX-R27-THREADS: sample unavailable: {exception.Message}");
        }
    }

    private static void ReportR27Resources()
    {
        if (!EnableR27ThreadMetrics)
        {
            return;
        }

        // Approximate observation only. Command states are externally synchronized
        // by their guest owners; never enumerate their mutable lists or dereference
        // native handles from the diagnostic thread.
        long states = 0, segments = 0, views = 0;

        ulong uploadBytes = 0;

        foreach (var pair in CommandStates)
        {
            var state = pair.Value;

            states++;

            segments += state.MemoryBoundaries.Count;

            views += state.HostViews.Count;

            uploadBytes += Volatile.Read(ref state.UploadBytes);
        }

        LogR25(FormattableString.Invariant(
            $"[performance] IRX-R27-RESOURCES: states={states}; segments={segments}; hostRefs={views}; listUploadMiB={uploadBytes / 1048576.0:F2}; materializedMiB={Volatile.Read(ref _hostResourceBytes) / 1048576.0:F2}; managedMiB={GC.GetTotalMemory(false) / 1048576.0:F2}; managedCommittedMiB={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0:F2}; liveObjects={LiveObjects.Count}; approximate=True;"));
    }

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

            ReportR27Threads(process, now);

            ReportR27Resources();
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
        FrameEvent,
        FrameEventLock,
        FrameSleep,
        FrameSchedule,
        FrameStats,
        ClockCalibration,
        MemoryPublication,
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
