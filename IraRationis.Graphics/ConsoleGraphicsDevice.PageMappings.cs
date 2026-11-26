// SPDX-License-Identifier: GPL-3.0-or-later
// Queue ABI and range kinds cross-checked against DexNom/VIctory, victory-gpu/src/queue.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int PageMappingOldSlot = 29;
    private const int PageMappingNewSlot = 31;
    private const ulong MappingPageBytes = 64UL << 10;
    private const uint MaximumMappingBatches = 65536;
    private const int MaximumMappingRanges = 262144;
    private const ulong MaximumMappingPages = 1048576;
    private const int MappingFailure = unchecked((int)0x80004005);
    private static readonly Lock PageMappingGate = new();
    private static Func<ulong, ulong, ulong, bool>? _pageMapper;
    private static long _pageMappingRequests;

    // Registered by the HLE before guest image execution. Graphics must not
    // depend on the HLE assembly or treat CPU addresses as host GPU addresses.
    public static void ConfigurePageMapper(Func<ulong, ulong, ulong, bool> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        Volatile.Write(ref _pageMapper, mapper);
    }

    private static void RegisterPageMappingMethods(bool newer)
    {
        if (sizeof(PageMappingBatch) != 24 || sizeof(PageMappingRange) != 12)
        {
            throw new InvalidOperationException("Unexpected console page mapping ABI layout.");
        }

        var table = (nint*)(newer ? _queueNew : _queueOld);
        var slot = newer ? PageMappingNewSlot : PageMappingOldSlot;

        table[slot] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, ulong, uint, uint, int>)&CopyPageMappings;
    }

    private static void ValidatePageMappingRegistration()
    {
        var method = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, ulong, uint, uint, int>)&CopyPageMappings;

        if (((nint*)_queueOld)[PageMappingOldSlot] != method || ((nint*)_queueNew)[PageMappingNewSlot] != method)
        {
            throw new InvalidOperationException("Queue page mapping registration failed.");
        }
    }

    private static bool TryReadMappingRecord<T>(ulong address, out T result) where T : unmanaged
    {
        result = default;

        if (address == 0 || address > (ulong)nint.MaxValue - (ulong)sizeof(T))
        {
            return false;
        }

        T value = default;

        nuint read = 0;

        var size = (nuint)sizeof(T);

        if (ReadProcessMemory(-1, (void*)(nuint)address, &value, size, &read) == 0 || read != size)
        {
            return false;
        }

        result = value;

        return true;
    }

    private static bool TryMappingRun(ulong destination, ulong pool, uint poolPages, PageMappingRange range,
        out PageMappingRun run)
    {
        run = default;

        var bytes = range.Count * MappingPageBytes;

        if (range.Kind > 3 || destination == 0 || (destination & (MappingPageBytes - 1)) != 0 ||
            destination > (ulong)nint.MaxValue || bytes > (ulong)nint.MaxValue - destination)
        {
            return false;
        }

        ulong source = 0;

        if (range.Count != 0 && range.Kind is 0 or 1)
        {
            var required = range.Kind == 0 ? range.Count : 1U;

            if (pool == 0 || (pool & (MappingPageBytes - 1)) != 0 || pool > (ulong)nint.MaxValue ||
                poolPages * MappingPageBytes > (ulong)nint.MaxValue - pool ||
                range.Start > poolPages || required > poolPages - range.Start)
            {
                return false;
            }

            source = pool + range.Start * MappingPageBytes;
        }

        run = new PageMappingRun(range.Kind, destination, source, bytes);

        return true;
    }

    private static bool TryPlanPageMappings(uint count, ulong batches, ulong pool, uint poolPages,
        List<PageMappingRun> plan, out ulong pages)
    {
        pages = 0;

        if (count > MaximumMappingBatches || (count != 0 && batches == 0) ||
            batches > (ulong)nint.MaxValue || count * (ulong)sizeof(PageMappingBatch) > (ulong)nint.MaxValue - batches)
        {
            return false;
        }

        var rangeCount = 0;

        for (var index = 0U; index < count; index++)
        {
            if (!TryReadMappingRecord(batches + index * (ulong)sizeof(PageMappingBatch), out PageMappingBatch batch) ||
                batch.Count > (uint)(MaximumMappingRanges - rangeCount) ||
                (batch.Count != 0 && batch.Ranges == 0) || batch.Ranges > (ulong)nint.MaxValue ||
                batch.Count * (ulong)sizeof(PageMappingRange) > (ulong)nint.MaxValue - batch.Ranges)
            {
                return false;
            }

            rangeCount += (int)batch.Count;

            var destination = batch.Destination;

            for (var rangeIndex = 0U; rangeIndex < batch.Count; rangeIndex++)
            {
                if (!TryReadMappingRecord(batch.Ranges + rangeIndex * (ulong)sizeof(PageMappingRange),
                        out PageMappingRange range) || range.Count > MaximumMappingPages - pages ||
                    !TryMappingRun(destination, pool, poolPages, range, out var run))
                {
                    return false;
                }

                pages += range.Count;

                destination += run.Bytes;

                if (run.Bytes != 0 && run.Kind != 3)
                {
                    plan.Add(run);
                }
            }
        }

        return true;
    }

    private static bool ApplyPageMapping(PageMappingRun run, Func<ulong, ulong, ulong, bool> mapper)
    {
        if (run.Kind == 1)
        {
            for (var offset = 0UL; offset < run.Bytes; offset += MappingPageBytes)
            {
                if (!mapper(run.Destination + offset, run.Source, MappingPageBytes))
                {
                    return false;
                }
            }

            return true;
        }

        return mapper(run.Destination, run.Source, run.Bytes);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CopyPageMappings(nint self, uint count, nint batches, ulong pool, uint poolPages, uint flags)
    {
        if (!LiveObjects.TryGetValue(self, out var table) || (table != _queueOld && table != _queueNew))
        {
            return InvalidArgument;
        }

        var mapper = Volatile.Read(ref _pageMapper);
        var queue = Host(self);

        if (mapper == null || queue == 0)
        {
            Console.Error.WriteLine("[graphics] IRX-GFX-PAGES-R15: physical page mapper or host queue unavailable.");

            return InvalidArgument;
        }

        var request = Interlocked.Increment(ref _pageMappingRequests);

        try
        {
            var plan = new List<PageMappingRun>();

            if (!TryPlanPageMappings(count, (nuint)batches, pool, poolPages, plan, out var pages))
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-PAGES-R15: invalid batch #{request}, count={count}, poolPages={poolPages}, flags=0x{flags:X}.");

                return InvalidArgument;
            }

            // Synchronous queue boundary: previously enqueued host work must
            // complete before the CPU aliases change. Cross-queue ordering and
            // GPU-written texture coherence still require their own implementation.
            lock (PageMappingGate)
            {
                if (plan.Count != 0)
                {
                    WaitForHostCompletion(queue);
                }

                foreach (var run in plan)
                {
                    if (!ApplyPageMapping(run, mapper))
                    {
                        // Earlier runs may already have committed. Do not claim
                        // success or attempt an unsafe rollback with stale mappings.
                        Console.Error.WriteLine(
                            $"[graphics] IRX-GFX-PAGES-R15: mapping failed #{request}, kind={run.Kind}, destination=0x{run.Destination:X}, source=0x{run.Source:X}, bytes={run.Bytes}; batch may be partially applied.");

                        return MappingFailure;
                    }
                }
            }

            if (request <= 8 || (request & (request - 1)) == 0 || flags != 0)
            {
                Console.Error.WriteLine(
                    $"[graphics] IRX-GFX-PAGES-R15: APPLIED batch #{request}, batches={count}, runs={plan.Count}, pages={pages}, flags=0x{flags:X} (CPU aliases; flag-specific semantics not translated).");
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[graphics] IRX-GFX-PAGES-R15: batch #{request} failed: {exception.Message}");

            return MappingFailure;
        }
    }

    private static void VerifyPageMappingPlan()
    {
        const ulong pool = 0x100000;
        const ulong destination = 0x200000;

        var ranges = stackalloc PageMappingRange[4]
        {
            new() { Kind = 0, Start = 2, Count = 2 },
            new() { Kind = 1, Start = 1, Count = 3 },
            new() { Kind = 2, Count = 1 },
            new() { Kind = 3, Count = 2 }
        };
        var batch = new PageMappingBatch { Destination = destination, Count = 4, Ranges = (ulong)ranges };
        var plan = new List<PageMappingRun>();

        if (!TryPlanPageMappings(1, (ulong)&batch, pool, 4, plan, out var pages) || pages != 8 || plan.Count != 3 ||
            plan[0] != new PageMappingRun(0, destination, pool + 2 * MappingPageBytes, 2 * MappingPageBytes) ||
            plan[1] != new PageMappingRun(1, destination + 2 * MappingPageBytes, pool + MappingPageBytes,
                3 * MappingPageBytes) ||
            plan[2] != new PageMappingRun(2, destination + 5 * MappingPageBytes, 0, MappingPageBytes))
        {
            throw new InvalidOperationException("Page mapping batch planning regression failed.");
        }

        var calls = new List<(ulong Destination, ulong Source, ulong Bytes)>();

        foreach (var run in plan)
        {
            if (!ApplyPageMapping(run, (to, from, bytes) =>
                {
                    calls.Add((to, from, bytes));

                    return true;
                }))
            {
                throw new InvalidOperationException("Page mapping dispatch regression failed.");
            }
        }

        if (calls.Count != 5 || calls[1].Source != calls[3].Source
                             || calls[3].Destination != destination + 4 * MappingPageBytes || calls[4].Source != 0)
        {
            throw new InvalidOperationException("Page mapping alias dispatch regression failed.");
        }

        ranges[0].Start = 3;

        plan.Clear();

        if (TryPlanPageMappings(1, (ulong)&batch, pool, 4, plan, out _) ||
            TryMappingRun(destination + 1, pool, 4, ranges[0], out _) ||
            TryReadMappingRecord(0, out PageMappingBatch _))
        {
            throw new InvalidOperationException("Invalid page mapping accepted.");
        }

        Console.WriteLine(
            "[graphics-probe] R15 queue page mapping ABI, range planning and alias dispatch passed (CPU only).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PageMappingBatch
    {
        public ulong Destination;
        public uint Count;
        public ulong Ranges;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PageMappingRange
    {
        public uint Kind;
        public uint Start;
        public uint Count;
    }

    private readonly record struct PageMappingRun(uint Kind, ulong Destination, ulong Source, ulong Bytes);
}
