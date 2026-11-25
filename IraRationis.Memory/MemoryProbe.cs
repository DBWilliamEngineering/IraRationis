// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace IraRationis.Memory;

public static unsafe partial class MemoryProbe
{
    private const ulong GuestBase = 0x100000000;
    private const int PageWords = (int)(VirtualPageTable.PageSize / sizeof(uint));
    private const int RandomReads = 4096;
    private const int Unsupported = unchecked((int)0x887A0004);

    [StructLayout(LayoutKind.Sequential)]
    public struct Sample
    {
        public double StagingMs;
        public double CopyMs;
        public double ComputeMs;
        public double SubmitMs;
        public uint Mismatches;
        public uint Reserved;
    }

    [LibraryImport("irx-native", EntryPoint = "irx_memory_create")]
    private static partial int Create(uint adapter, uint mode, ulong bytes, out nint session, out nuint cpu, out ulong gpu);

    [LibraryImport("irx-native", EntryPoint = "irx_memory_run")]
    private static partial int Run(nint session, uint seed, out Sample sample);

    [LibraryImport("irx-native", EntryPoint = "irx_memory_destroy")]
    private static partial void Destroy(nint session);

    [LibraryImport("irx-native", EntryPoint = "irx_memory_adapter")]
    private static partial int Adapter(uint index, char* description, uint capacity, out ulong dedicatedBytes);

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

        var sizes = new[] { 16, 64, 256 };
        var iterations = 5;
        var adapter = 0U;

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];

            if (i + 1 >= args.Length || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"Expected an integer for {option}.");
            }

            switch (option)
            {
                case "--mib" when value is >= 1 and <= 256:
                    sizes = [value];
                    break;
                case "--iterations" when value is >= 1 and <= 100:
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

        var name = new char[128];
        int status;
        ulong vram;

        fixed (char* description = name)
        {
            status = Adapter(adapter, description, 128, out vram);
        }

        if (status < 0)
        {
            throw new InvalidOperationException($"Adapter {adapter} discovery failed: 0x{status:X8}.");
        }

        writer.WriteLine($"Adapter {adapter}: {new string(name.AsSpan(0, Array.IndexOf(name, '\0')))}; dedicated memory {vram / (1024 * 1024)} MiB");
        writer.WriteLine("Each mode uses identical full-buffer writes and GPU checksum dispatches; one warmup is excluded.");
        writer.WriteLine("CPU read latency is a separate sampled random-read test. GPU copy/compute use GPU timestamps.");
        writer.WriteLine("mode,mib,iteration,cpu_write_ms,staging_ms,gpu_copy_ms,gpu_compute_ms,submit_wait_ms,cycle_ms,random_read_ns,checksum_errors");

        var failed = false;

        foreach (var size in sizes)
        {
            foreach (var mode in Enum.GetValues<MemoryResidency>())
            {
                if (!Measure(mode, size, iterations, adapter, writer))
                {
                    failed = true;
                }
            }
        }

        return failed ? 1 : 0;
    }

    private static bool Measure(MemoryResidency mode, int mib, int iterations, uint adapter, TextWriter writer)
    {
        var bytes = (ulong)mib * 1024 * 1024;
        var status = Create(adapter, (uint)mode, bytes, out var session, out var cpu, out var gpu);

        if (status < 0)
        {
            var optional = mode == MemoryResidency.ExistingSystemHeap || (mode == MemoryResidency.GpuUpload && status == Unsupported);
            writer.WriteLine($"# {mode} {mib} MiB: {(optional ? "SKIPPED" : "FAILED")} HRESULT 0x{status:X8}");

            return optional;
        }

        var table = new VirtualPageTable();

        try
        {
            table.Map(GuestBase, cpu, gpu, bytes, mode);
            writer.WriteLine($"# {mode}: guest=0x{GuestBase:X}, CPU=0x{cpu:X}, GPU=0x{gpu:X}");

            for (var iteration = 0; iteration <= iterations; iteration++)
            {
                var seed = (uint)(iteration + 17);
                var cycleStart = Stopwatch.GetTimestamp();
                var writeStart = cycleStart;

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
                var writeMs = Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds;
                status = Run(session, seed, out var sample);
                var cycleMs = Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;

                if (status < 0)
                {
                    writer.WriteLine($"# {mode} dispatch FAILED: 0x{status:X8}");

                    return false;
                }

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
                writer.WriteLine(FormattableString.Invariant($"{mode},{mib},{iteration},{writeMs:F4},{sample.StagingMs:F4},{sample.CopyMs:F4},{sample.ComputeMs:F4},{sample.SubmitMs:F4},{cycleMs:F4},{randomNs:F2},{sample.Mismatches}"));
            }

            table.Unmap(GuestBase, bytes);

            return true;
        }
        finally
        {
            Destroy(session);
        }
    }

    private static double ReadLatency(nuint cpu, ulong bytes, uint seed)
    {
        var pointer = (uint*)cpu;
        var count = (uint)(bytes / sizeof(uint));
        var state = 0x12345678U;
        var sum = 0UL;
        var expected = 0UL;
        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < RandomReads; i++)
        {
            state = unchecked(state * 1664525 + 1013904223);
            var index = state % count;
            sum += Volatile.Read(ref pointer[index]);
        }

        var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds / RandomReads;
        state = 0x12345678U;

        for (var i = 0; i < RandomReads; i++)
        {
            state = unchecked(state * 1664525 + 1013904223);
            var index = state % count;
            expected += seed + index / PageWords;
        }

        if (sum != expected)
        {
            throw new InvalidOperationException("CPU readback did not match the expected synthetic pattern.");
        }

        return elapsed;
    }

    private static void SelfTest()
    {
        var table = new VirtualPageTable();
        var size = VirtualPageTable.PageSize;
        table.Map(GuestBase, 0x200000, 0x300000, size * 2, MemoryResidency.GpuUpload);

        if (!table.TryTranslate(GuestBase + size + 7, out var page) || page.CpuAddress != 0x210007 || page.GpuAddress != 0x310007 || page.Residency != MemoryResidency.GpuUpload)
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
