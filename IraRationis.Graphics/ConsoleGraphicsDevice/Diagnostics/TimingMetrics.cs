// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using IraRationis.Graphics.Diagnostics.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static partial class ConsoleGraphicsDevice
{
    private static readonly bool EnableTiming = Environment.GetEnvironmentVariable("IRX_PROFILE_TIMING") != "0";
    internal static readonly GraphicsTimingCounter[] GraphicsTimingCounters = CreateGraphicsTimingCounters();
    private static readonly object TimingReportGate = new();
    private static Timer? _timingReportTimer;
    private static int _timingInitialized;
    private static long _lastTimingReport;
    private static long _previousCpuTicks;
    internal static long _lastPresentExit;
    private static readonly bool EnableThreadMetrics =
        Environment.GetEnvironmentVariable("IRX_PROFILE_THREADS") != "0";
    private static readonly Dictionary<int, (long StartTicks, long CpuTicks)> PreviousThreadCpuSamples = new();
    private static long _previousThreadSample;
    private static readonly long DetailReportIntervalTicks = ReadDetailReportInterval();
    private static long _lastDetailReport;

    private static long ReadDetailReportInterval()
    {
        var value = Environment.GetEnvironmentVariable("IRX_PROFILE_DETAIL_INTERVAL_MS");

        if (!int.TryParse(value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var milliseconds))
        {
            return -1;
        }

        if (milliseconds == 0)
        {
            return 0;
        }

        if (milliseconds is < 2000 or > 60000)
        {
            return -1;
        }

        return Stopwatch.Frequency * milliseconds / 1000;
    }

    // Called only by the existing report timer under TimingReportGate. This is a
    // sampled view of OS-thread CPU time, not a stack profiler or wait trace.
    private static void ReportThreads(Process process, long now)
    {
        if (!EnableThreadMetrics)
        {
            return;
        }

        try
        {
            var previous = _previousThreadSample;
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

                    if (elapsed > 0 && PreviousThreadCpuSamples.TryGetValue(id, out var old) &&
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

            PreviousThreadCpuSamples.Clear();

            foreach (var pair in current)
            {
                PreviousThreadCpuSamples.Add(pair.Key, pair.Value);
            }

            _previousThreadSample = now;

            hot.Sort(static (left, right) => right.Cores.CompareTo(left.Cores));

            var report = new StringBuilder(FormattableString.Invariant(
                $"[performance] IRX-R27-THREADS: interval={elapsed:F2}s; sampled={current.Count}; missed={missed};"));

            for (var index = 0; index < Math.Min(5, hot.Count); index++)
            {
                var item = hot[index];

                report.Append(FormattableString.Invariant(
                    $" tid={item.Id}(0x{item.Id:X}),cores={item.Cores:F3},state={item.State};"));
            }

            LogPerformance(report.ToString());
        }
        catch (Exception exception)
        {
            LogPerformance($"[performance] IRX-R27-THREADS: sample unavailable: {exception.Message}");
        }
    }

    private static void ReportResources()
    {
        if (!EnableThreadMetrics)
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

        LogPerformance(FormattableString.Invariant(
            $"[performance] IRX-R27-RESOURCES: states={states}; segments={segments}; hostRefs={views}; listUploadMiB={uploadBytes / 1048576.0:F2}; materializedMiB={Volatile.Read(ref _hostResourceBytes) / 1048576.0:F2}; managedMiB={GC.GetTotalMemory(false) / 1048576.0:F2}; managedCommittedMiB={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0:F2}; liveObjects={LiveObjects.Count}; approximate=True;"));
    }

    private static GraphicsTimingCounter[] CreateGraphicsTimingCounters()
    {
        var counters = new GraphicsTimingCounter[(int)GraphicsStage.Count];

        for (var index = 0; index < counters.Length; index++)
        {
            counters[index] = new GraphicsTimingCounter();
        }

        return counters;
    }

    internal static GraphicsTimingScope BeginTiming(GraphicsStage stage)
    {
        if (!EnableTiming)
        {
            return default;
        }

        if (Volatile.Read(ref _timingInitialized) == 0 &&
            Interlocked.CompareExchange(ref _timingInitialized, 1, 0) == 0)
        {
            try
            {
                using var process = Process.GetCurrentProcess();

                _previousCpuTicks = process.TotalProcessorTime.Ticks;

                _lastTimingReport = Stopwatch.GetTimestamp();

                _timingReportTimer = new Timer(static _ => ReportTiming(), null, 2000, 2000);

                LogPerformance(
                    "[performance] IRX-R25: timing active; inclusive thread-wall timings, not additive; period=2s.");
            }
            catch (Exception exception)
            {
                LogPerformance($"[performance] IRX-R25: periodic reporting unavailable: {exception.Message}");
            }
        }

        var now = Stopwatch.GetTimestamp();

        if (stage == GraphicsStage.PresentX)
        {
            var previous = Interlocked.Exchange(ref _lastPresentExit, 0);

            if (previous != 0 && now >= previous)
            {
                RecordTiming(GraphicsStage.PresentGap, now - previous);
            }
        }

        Interlocked.Increment(ref GraphicsTimingCounters[(int)stage].Active);

        return new GraphicsTimingScope(stage, now, true, BeginCpuSample(stage));
    }

    internal static void RecordTiming(GraphicsStage stage, long ticks)
    {
        var counter = GraphicsTimingCounters[(int)stage];

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
        if (stage != GraphicsStage.PresentGap && ticks >= Stopwatch.Frequency / 10)
        {
            var slow = Interlocked.Increment(ref counter.Slow);

            if (slow <= 4 || (slow & (slow - 1)) == 0)
            {
                LogPerformance(FormattableString.Invariant(
                    $"[performance] IRX-R25-SLOW: stage={stage}; ms={ticks * 1000.0 / Stopwatch.Frequency:F3}; thread={Environment.CurrentManagedThreadId}; occurrences={slow}."));
            }
        }
    }

    private static void LogPerformance(string message)
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

    private static void ReportTiming()
    {
        if (!Monitor.TryEnter(TimingReportGate))
        {
            return;
        }

        try
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastTimingReport) / (double)Stopwatch.Frequency;

            if (elapsed < 1)
            {
                return;
            }

            using var process = Process.GetCurrentProcess();

            var cpuTicks = process.TotalProcessorTime.Ticks;
            var cpuCores = (cpuTicks - _previousCpuTicks) / (double)TimeSpan.TicksPerSecond / elapsed;
            var report = new StringBuilder(FormattableString.Invariant(
                $"[performance] IRX-R25: interval={elapsed:F2}s; cpuCoreEquiv={cpuCores:F2}; privateMiB={process.PrivateMemorySize64 / 1048576.0:F0}; workingSetMiB={process.WorkingSet64 / 1048576.0:F0}; gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)};"));

            for (var index = 0; index < GraphicsTimingCounters.Length; index++)
            {
                var counter = GraphicsTimingCounters[index];
                var calls = Interlocked.Read(ref counter.Calls);
                var ticks = Interlocked.Read(ref counter.Ticks);
                var maximum = Interlocked.Exchange(ref counter.Maximum, 0);
                var active = Interlocked.Read(ref counter.Active);
                var count = calls - counter.PreviousCalls;

                if (count != 0 || active != 0)
                {
                    report.Append(FormattableString.Invariant(
                        $" {(GraphicsStage)index}[n={count},ms={(ticks - counter.PreviousTicks) * 1000.0 / Stopwatch.Frequency:F2},max={maximum * 1000.0 / Stopwatch.Frequency:F2},active={active}];"));
                }

                counter.PreviousCalls = calls;

                counter.PreviousTicks = ticks;
            }

            _previousCpuTicks = cpuTicks;

            _lastTimingReport = now;

            LogPerformance(report.ToString());

            ReportCpuSamples();

            // Detailed OS-thread and resource enumeration has a separate
            // cadence. Preserve the ordinary stage counters at their original rate.
            if (EnableThreadMetrics && DetailReportIntervalTicks != 0 &&
                (DetailReportIntervalTicks < 0 || _lastDetailReport == 0 ||
                 now - _lastDetailReport >= DetailReportIntervalTicks))
            {
                _lastDetailReport = now;

                ReportThreads(process, now);

                ReportResources();
            }
        }
        catch (Exception exception)
        {
            // Diagnostic reporting must never terminate the title.
            LogPerformance($"[performance] IRX-R25: report failed: {exception.Message}");
        }
        finally
        {
            Monitor.Exit(TimingReportGate);
        }
    }
}
