/*
* @file        MemoryProbe.cs
* @description Measures the CPU and GPU costs of standalone D3D12 memory modes.
* @author      Damon William
* @date        2026-11-27
*/
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IraRationis.Memory.VirtualMemory;
using IraRationis.Memory.VirtualMemory.Models;

namespace IraRationis.Cli.Diagnostics.Memory;

internal static unsafe class MemoryProbe
{
    private const ulong GuestBase = 0x100000000;
    private const int PageWords = (int)(VirtualPageTable.PageSize / sizeof(uint));
    private const int RandomReads = 4096;
    private const int BytesPerMiB = 1024 * 1024;
    private const int MaximumSizeMiB = 256;
    private const int DefaultIterations = 5;
    private const int MaximumIterations = 100;
    private const uint FirstIterationSeed = 17;
    private const uint ReadSequenceSeed = 0x12345678;
    private const uint ReadSequenceMultiplier = 1664525;
    private const uint ReadSequenceIncrement = 1013904223;

    public static int Execute(string[] args, TextWriter writer)
    {
        if (args is ["--self-test"])
        {
            SelfTest();

            writer.WriteLine("Memory page-table tests passed.");

            return 0;
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("The D3D12 memory probe requires Windows x64.");
        }

        var sizes = new[] { 16, 64, MaximumSizeMiB };
        var iterations = DefaultIterations;
        var adapter = 0U;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];

            if (index + 1 >= args.Length
                || !int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"Expected an integer for {option}.");
            }

            switch (option)
            {
                case "--mib" when value is >= 1 and <= MaximumSizeMiB:
                    sizes = [value];

                    break;

                case "--iterations" when value is >= 1 and <= MaximumIterations:
                    iterations = value;

                    break;

                case "--adapter" when value >= 0:
                    adapter = (uint)value;

                    break;

                default:
                    throw new ArgumentException($"Invalid probe argument: {option} {value}.");
            }
        }

        SelfTest();

        string name;
        ulong dedicatedMemoryBytes;
        try
        {
            D3D12MemoryProbeSession.ReadAdapter(adapter, out name, out dedicatedMemoryBytes);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException($"Adapter {adapter} discovery failed: 0x{exception.HResult:X8}.",
                exception);
        }

        writer.WriteLine(
            $"Adapter {adapter}: {name}; dedicated memory {dedicatedMemoryBytes / BytesPerMiB} MiB");

        writer.WriteLine(
            "Each mode uses identical full-buffer writes and GPU checksum dispatches; one warmup is excluded.");

        writer.WriteLine(
            "CPU read latency is a separate sampled random-read test. GPU copy/compute use GPU timestamps.");

        writer.WriteLine(
            "mode,mib,iteration,cpu_write_ms,staging_ms,gpu_copy_ms,gpu_compute_ms,submit_wait_ms,cycle_ms,random_read_ns,checksum_errors");

        var failed = false;

        var modes = Enum.GetValues<MemoryResidency>();
        foreach (var size in sizes)
        {
            foreach (var mode in modes)
            {
                failed |= !Measure(mode, size, iterations, adapter, writer);
            }
        }

        return failed ? 1 : 0;
    }

    private static bool Measure(MemoryResidency mode, int mib, int iterations, uint adapter, TextWriter writer)
    {
        var bytes = (ulong)mib * BytesPerMiB;
        using var session = new D3D12MemoryProbeSession(mode, bytes);

        try
        {
            session.Initialize(adapter);
        }
        catch (COMException exception)
        {
            var optional = mode == MemoryResidency.ExistingSystemHeap
                           || (mode == MemoryResidency.GpuUpload &&
                               exception.HResult == D3D12MemoryProbeSession.Unsupported);

            writer.WriteLine($"# {mode} {mib} MiB: {(optional ? "SKIPPED" : "FAILED")} HRESULT 0x{exception.HResult:X8}");

            return optional;
        }

        var cpu = session.CpuAddress;
        var gpu = session.GpuAddress;
        var table = new VirtualPageTable();

        table.Map(GuestBase, cpu, gpu, bytes, mode);

        try
        {
            writer.WriteLine($"# {mode}: guest=0x{GuestBase:X}, CPU=0x{cpu:X}, GPU=0x{gpu:X}");

            for (var iteration = 0; iteration <= iterations; iteration++)
            {
                var seed = (uint)iteration + FirstIterationSeed;
                var cycleStart = Stopwatch.GetTimestamp();

                for (var offset = 0UL; offset < bytes; offset += VirtualPageTable.PageSize)
                {
                    if (!table.TryTranslate(GuestBase + offset, out var page))
                    {
                        throw new InvalidOperationException("Missing page during benchmark write.");
                    }

                    var words = new Span<uint>((void*)page.CpuAddress, PageWords);

                    words.Fill(seed + (uint)(offset / VirtualPageTable.PageSize));
                }

                Thread.MemoryBarrier();

                var writeMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;

                var sample = session.Run(seed);
                var cycleMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;

                if (sample.Mismatches != 0)
                {
                    writer.WriteLine($"# {mode}: GPU checksum failed ({sample.Mismatches} groups).");

                    return false;
                }

                if (iteration == 0)
                {
                    continue;
                }

                // Do not include these reads in the upload or GPU timing.
                var randomNs = ReadLatency(cpu, bytes, seed);

                writer.WriteLine(FormattableString.Invariant(
                    $"{mode},{mib},{iteration},{writeMs:F4},{sample.StagingMs:F4},{sample.CopyMs:F4},{sample.ComputeMs:F4},{sample.SubmitMs:F4},{cycleMs:F4},{randomNs:F2},{sample.Mismatches}"));
            }

            return true;
        }
        catch (COMException exception)
        {
            writer.WriteLine($"# {mode} dispatch FAILED: 0x{exception.HResult:X8}");

            return false;
        }
        finally
        {
            table.Unmap(GuestBase, bytes);
        }
    }

    private static double ReadLatency(nuint cpu, ulong bytes, uint seed)
    {
        var pointer = (uint*)cpu;
        var count = (uint)(bytes / sizeof(uint));
        var state = ReadSequenceSeed;
        var sum = 0UL;
        var expected = 0UL;
        var start = Stopwatch.GetTimestamp();

        for (var elementIndex = 0; elementIndex < RandomReads; elementIndex++)
        {
            state = unchecked(state * ReadSequenceMultiplier + ReadSequenceIncrement);

            var index = state % count;

            sum += Volatile.Read(ref pointer[index]);
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / RandomReads;

        state = ReadSequenceSeed;

        for (var elementIndex = 0; elementIndex < RandomReads; elementIndex++)
        {
            state = unchecked(state * ReadSequenceMultiplier + ReadSequenceIncrement);

            var index = state % count;

            expected += seed + index / PageWords;
        }

        return sum != expected
            ? throw new InvalidOperationException("CPU readback did not match the expected synthetic pattern.")
            : elapsed;
    }

    private static void SelfTest()
    {
        var table = new VirtualPageTable();

        const ulong size = VirtualPageTable.PageSize;

        table.Map(GuestBase, 0x200000, 0x300000, size * 2, MemoryResidency.GpuUpload);

        if (!table.TryTranslate(GuestBase + size + 7, out var page) || page.CpuAddress != 0x210007
                                                                    || page.GpuAddress != 0x310007
                                                                    || page.Residency != MemoryResidency.GpuUpload)
        {
            throw new InvalidOperationException("Page offset translation failed.");
        }

        var rejected = false;

        try
        {
            table.Map(GuestBase + size, 0x400000, 0x500000, size * 2, MemoryResidency.SystemUpload);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        if (!rejected || table.TryTranslate(GuestBase + size * 2, out _))
        {
            throw new InvalidOperationException("Overlapping mapping was not rejected atomically.");
        }

        table.Unmap(GuestBase, size * 2);

        if (table.TryTranslate(GuestBase, out _))
        {
            throw new InvalidOperationException("Unmap failed.");
        }
    }
}
