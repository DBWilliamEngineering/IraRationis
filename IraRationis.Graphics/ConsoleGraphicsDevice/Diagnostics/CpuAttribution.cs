// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using IraRationis.Graphics.Diagnostics.Models;

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    private const int CpuSampleStride = 64;
    private static readonly bool EnableCpuSampling = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("IRX_PROFILE_HOST_CPU") == "1";
    private static readonly CpuCounter[] CpuCounters = CreateCpuCounters();

    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadTimes")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetThreadTimes(nint thread, out long creation, out long exit,
        out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetThreadId();

    private static CpuCounter[] CreateCpuCounters()
    {
        var counters = new CpuCounter[(int)GraphicsStage.Count];

        for (var index = 0; index < counters.Length; index++)
        {
            counters[index] = new CpuCounter();
        }

        return counters;
    }

    private static bool TryReadCpu(out long ticks)
    {
        ticks = 0;

        // Windows pseudo-handle for the calling thread. No handle ownership.
        if (GetThreadTimes((nint)(-2), out _, out _, out var kernel, out var user) == 0 ||
            kernel < 0 || user < 0 || kernel > long.MaxValue - user)
        {
            return false;
        }

        ticks = kernel + user;

        return true;
    }

    private static CpuSample BeginCpuSample(GraphicsStage stage)
    {
        if (!EnableCpuSampling)
        {
            return default;
        }

        var counter = CpuCounters[(int)stage];
        var call = Interlocked.Increment(ref counter.Calls);

        if (call != 1 && (call & (CpuSampleStride - 1)) != 0)
        {
            return default;
        }

        if (!TryReadCpu(out var ticks))
        {
            Interlocked.Increment(ref counter.Failed);

            return default;
        }

        return new CpuSample(GetThreadId(), ticks, Stopwatch.GetTimestamp());
    }

    internal static void EndCpuSample(GraphicsStage stage, CpuSample sample)
    {
        if (sample.ThreadId == 0)
        {
            return;
        }

        var counter = CpuCounters[(int)stage];
        var now = Stopwatch.GetTimestamp();

        if (GetThreadId() != sample.ThreadId || !TryReadCpu(out var ticks) ||
            ticks < sample.CpuTicks || now < sample.WallTimestamp)
        {
            Interlocked.Increment(ref counter.Failed);

            return;
        }

        Interlocked.Add(ref counter.CpuTicks, ticks - sample.CpuTicks);
        Interlocked.Add(ref counter.WallTicks, now - sample.WallTimestamp);
        Interlocked.Increment(ref counter.Completed);
    }

    private static void ReportCpuSamples()
    {
        if (!EnableCpuSampling)
        {
            return;
        }

        var report = new StringBuilder("[performance] IRX-R71-HOST-CPU: cumulative=True; inclusive=True; approximate=True; stride=64;");

        for (var index = 0; index < CpuCounters.Length; index++)
        {
            var counter = CpuCounters[index];
            var calls = Interlocked.Read(ref counter.Calls);

            if (calls == 0)
            {
                continue;
            }

            var completed = Interlocked.Read(ref counter.Completed);
            var failed = Interlocked.Read(ref counter.Failed);
            var cpu = Interlocked.Read(ref counter.CpuTicks) / (double)TimeSpan.TicksPerMillisecond;
            var wall = Interlocked.Read(ref counter.WallTicks) * 1000.0 / Stopwatch.Frequency;

            report.Append(FormattableString.Invariant(
                $" {(GraphicsStage)index}[calls={calls},samples={completed},failed={failed},cpuMs={cpu:F3},wallMs={wall:F3}];"));
        }

        LogPerformance(report.ToString());
    }
}
