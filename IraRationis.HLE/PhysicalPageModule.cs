// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/xmem.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IraRationis.HLE;

public static unsafe partial class PhysicalPageModule
{
    private const ulong PageSize = 65536;
    private const ulong FramesPerPage = 16;
    private const ulong LastPage = 1UL << 25;
    private const uint PageReadWrite = 4;
    private const uint SectionReserve = 0x04000000;
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint MemPreservePlaceholder = 2;
    private const uint MemReplacePlaceholder = 0x4000;
    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, Slice> Pages = [];
    private static readonly SortedDictionary<ulong, View> Views = [];
    private static ulong _nextPage = 0x100000;

    public static HleModule Create()
    {
        return new HleModule("physical-pages", ["xmem.dll"],
        [
            new HleExport("XMemAllocatePhysicalPages",
                (nuint)(delegate* unmanaged[Stdcall]<ulong, nuint*, nuint*, int>)&Allocate),
            new HleExport("XMemFreePhysicalPages", (nuint)(delegate* unmanaged[Stdcall]<nuint, nuint*, int>)&Free),
            new HleExport("XMemMapPhysicalPages", (nuint)(delegate* unmanaged[Stdcall]<nint, nuint, nuint*, nint>)&Map),
            new HleExport("XMemMapPhysicalPagesScatter",
                (nuint)(delegate* unmanaged[Stdcall]<nint*, nuint, nuint*, int>)&Scatter)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Allocate(ulong flags, nuint* count, nuint* pages)
    {
        if (count == null || pages == null)
        {
            return 0;
        }

        var requested = (ulong)*count;

        *count = 0;

        try
        {
            lock (Gate)
            {
                if (requested == 0 || requested > (LastPage - _nextPage) / FramesPerPage)
                {
                    return 0;
                }

                var size = requested * PageSize;
                var handle = CreateFileMappingW(-1, 0, PageReadWrite | SectionReserve, (uint)(size >> 32), (uint)size,
                    null);

                if (handle == 0)
                {
                    Report("CreateFileMappingW", 0, size);

                    return 0;
                }

                var section = new SafeFileHandle(handle, true);
                var first = _nextPage;

                ulong inserted = 0;

                try
                {
                    for (ulong index = 0; index < requested; index++)
                    {
                        var page = first + index * FramesPerPage;

                        Pages.Add(page, new Slice(section, index));

                        inserted++;

                        pages[index] = (nuint)page;
                    }
                }
                catch
                {
                    for (ulong index = 0; index < inserted; index++)
                    {
                        Pages.Remove(first + index * FramesPerPage);
                    }

                    section.Dispose();

                    throw;
                }

                _nextPage += requested * FramesPerPage;

                *count = (nuint)requested;

                Console.WriteLine(
                    $"[xmem] Allocated {requested} physical pages ({size >> 20} MiB reserved, flags 0x{flags:X}).");

                return 1;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[xmem] Physical allocation failed: {exception.Message}");

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Free(nuint count, nuint* pages)
    {
        if (pages == null)
        {
            return 0;
        }

        lock (Gate)
        {
            for (nuint index = 0; index < count; index++)
            {
                // Existing views retain their section reference.
                Pages.Remove(pages[index]);
            }
        }

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Map(nint address, nuint count, nuint* pages)
    {
        try
        {
            lock (Gate)
            {
                return MapCore(address, count, pages);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[xmem] Map failed: {exception.Message}");

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Scatter(nint* addresses, nuint count, nuint* pages)
    {
        if (addresses == null)
        {
            return 0;
        }

        try
        {
            lock (Gate)
            {
                for (nuint index = 0; index < count; index++)
                {
                    var page = pages == null || pages[index] == 0 ? null : pages + index;

                    if (MapCore(addresses[index], 1, page) == 0)
                    {
                        return 0;
                    }
                }
            }

            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[xmem] Scatter failed: {exception.Message}");

            return 0;
        }
    }

    private static nint MapCore(nint address, ulong count, nuint* pages)
    {
        var start = (ulong)(nuint)address;

        if (address == 0 || (start & (PageSize - 1)) != 0 || count > (ulong.MaxValue - start) / PageSize
            || count > LastPage / FramesPerPage)
        {
            return 0;
        }

        if (count == 0)
        {
            return address;
        }

        // Resolve all physical IDs before changing the virtual mappings.
        var runs = new List<Run>();

        if (pages != null)
        {
            for (ulong index = 0; index < count; index++)
            {
                if (!Pages.TryGetValue(pages[index], out var slice))
                {
                    return 0;
                }

                AppendRun(runs, slice);
            }
        }

        if (!UnmapRange(start, count * PageSize))
        {
            return 0;
        }

        var current = start;

        foreach (var run in runs)
        {
            var length = run.Count * PageSize;

            if (!MapView(current, length, run.First))
            {
                return 0;
            }

            current += length;
        }

        return address;
    }

    private static void AppendRun(List<Run> runs, Slice slice)
    {
        if (runs.Count > 0)
        {
            var previous = runs[^1];

            if (ReferenceEquals(previous.First.Section, slice.Section)
                && previous.First.Index + previous.Count == slice.Index)
            {
                runs[^1] = previous with
                {
                    Count = previous.Count + 1
                };

                return;
            }
        }

        runs.Add(new Run(slice, 1));
    }

    private static bool UnmapRange(ulong start, ulong size)
    {
        if (size == 0)
        {
            return true;
        }

        var end = start + size;
        var overlaps = Views.Where(pair => pair.Key < end && pair.Key + pair.Value.Size > start).ToArray();

        foreach (var (address, view) in overlaps)
        {
            if (UnmapViewOfFile2(GetCurrentProcess(), (nint)address, MemPreservePlaceholder) == 0)
            {
                Report("UnmapViewOfFile2", address, view.Size);

                return false;
            }

            Views.Remove(address);

            var viewEnd = address + view.Size;

            if (address < start && !MapView(address, start - address, view.Slice))
            {
                return false;
            }

            if (viewEnd > end && !MapView(end, viewEnd - end,
                    view.Slice with
                    {
                        Index = view.Slice.Index + (end - address) / PageSize
                    }))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MapView(ulong address, ulong size, Slice slice)
    {
        // Split a larger placeholder to fit this run. A complete placeholder
        // may reject the split; MapViewOfFile3 is the authoritative check.
        _ = VirtualFree((nint)address, (nuint)size, MemRelease | MemPreservePlaceholder);

        var process = GetCurrentProcess();
        var mapped = MapViewOfFile3(slice.Section.DangerousGetHandle(), process, (nint)address, slice.Index * PageSize,
            (nuint)size, MemReplacePlaceholder, PageReadWrite, 0, 0);

        GC.KeepAlive(slice.Section);

        if (mapped != (nint)address)
        {
            var error = Marshal.GetLastPInvokeError();

            if (mapped != 0)
            {
                _ = UnmapViewOfFile2(process, mapped, MemPreservePlaceholder);
            }

            // Adjacent placeholders may have been split by earlier scatter
            // operations. Try individual pages rather than assume contiguity.
            if (size > PageSize)
            {
                for (ulong offset = 0; offset < size; offset += PageSize)
                {
                    if (!MapView(address + offset, PageSize, slice with
                        {
                            Index = slice.Index + offset / PageSize
                        }))
                    {
                        return false;
                    }
                }

                return true;
            }

            Console.Error.WriteLine($"[xmem] MapViewOfFile3 failed at 0x{address:X}, size 0x{size:X}, Win32 {error}.");

            return false;
        }

        if (VirtualAlloc(mapped, (nuint)size, MemCommit, PageReadWrite) == 0)
        {
            Report("VirtualAlloc (physical commit)", address, size);

            _ = UnmapViewOfFile2(process, mapped, MemPreservePlaceholder);

            return false;
        }

        Views.Add(address, new View(size, slice));

        return true;
    }

    public static bool MapAlias(ulong destination, ulong source, ulong size)
    {
        if (destination == 0 || ((destination | source | size) & (PageSize - 1)) != 0
                             || size > ulong.MaxValue - destination || size > ulong.MaxValue - source)
        {
            return false;
        }

        lock (Gate)
        {
            var runs = new List<Run>();

            if (source != 0)
            {
                // Capture the source before unmapping; source and destination
                // may overlap and must still refer to the original pages.
                for (ulong offset = 0; offset < size; offset += PageSize)
                {
                    var address = source + offset;

                    Slice? slice = null;

                    foreach (var (start, view) in Views)
                    {
                        if (start > address)
                        {
                            break;
                        }

                        if (address - start < view.Size)
                        {
                            slice = view.Slice with
                            {
                                Index = view.Slice.Index + (address - start) / PageSize
                            };

                            break;
                        }
                    }

                    if (slice is null)
                    {
                        return false;
                    }

                    AppendRun(runs, slice);
                }
            }

            if (!UnmapRange(destination, size))
            {
                return false;
            }

            var current = destination;

            foreach (var run in runs)
            {
                var length = run.Count * PageSize;

                if (!MapView(current, length, run.First))
                {
                    return false;
                }

                current += length;
            }

            return true;
        }
    }

    // DirectStorage may write a physical page even when it has no title view.
    public static bool WritePhysical(nuint* pages, ulong offset, ReadOnlySpan<byte> data)
    {
        if (pages == null || offset >= PageSize)
        {
            return false;
        }

        lock (Gate)
        {
            if (data.IsEmpty)
            {
                return true;
            }

            var runs = new List<Run>();
            var count = (offset + (ulong)data.Length + PageSize - 1) / PageSize;

            for (ulong index = 0; index < count; index++)
            {
                if (!Pages.TryGetValue(pages[index], out var slice))
                {
                    return false;
                }

                AppendRun(runs, slice);
            }

            var done = 0;

            foreach (var run in runs)
            {
                var length = run.Count * PageSize;
                var sectionOffset = run.First.Index * PageSize;
                var view = MapViewOfFile(run.First.Section.DangerousGetHandle(), 2,
                    (uint)(sectionOffset >> 32), (uint)sectionOffset, (nuint)length);

                GC.KeepAlive(run.First.Section);

                if (view == 0)
                {
                    Report("MapViewOfFile (storage)", 0, length);

                    return false;
                }

                try
                {
                    if (VirtualAlloc(view, (nuint)length, MemCommit, PageReadWrite) == 0)
                    {
                        Report("VirtualAlloc (storage commit)", (nuint)view, length);

                        return false;
                    }

                    var bytes = (int)Math.Min(length - offset, (ulong)(data.Length - done));

                    data.Slice(done, bytes).CopyTo(new Span<byte>((byte*)view + (nuint)offset, bytes));

                    done += bytes;

                    offset = 0;
                }
                finally
                {
                    _ = UnmapViewOfFile(view);
                }
            }

            return done == data.Length;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UnmapViewOfFile(nint address);

    private static void Report(string function, ulong address, ulong size)
    {
        var error = Marshal.GetLastPInvokeError();

        Console.Error.WriteLine($"[xmem] {function} failed at 0x{address:X}, size 0x{size:X}, Win32 {error}.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateFileMappingW(nint file, nint attributes, uint protection, uint sizeHigh,
        uint sizeLow, char* name);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MapViewOfFile3(nint mapping, nint process, nint address, ulong offset, nuint size,
        uint allocation, uint protection, nint parameters, uint count);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UnmapViewOfFile2(nint process, nint address, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint VirtualAlloc(nint address, nuint size, uint allocation, uint protection);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int VirtualFree(nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    private sealed record Slice(SafeFileHandle Section, ulong Index);

    private sealed record View(ulong Size, Slice Slice);

    private sealed record Run(Slice First, ulong Count);
}
