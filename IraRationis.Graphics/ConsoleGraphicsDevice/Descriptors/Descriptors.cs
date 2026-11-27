// SPDX-License-Identifier: GPL-3.0-or-later
// Console descriptor ABI adapted from Zettrox/ztrx crates/victory-gpu/src/descriptor.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const uint DescriptorTag = 0x44585249; // IRXD: runtime-private token, not AMD hardware encoding.
    private const uint ConstantTag = 0x43585249; // IRXC
    private const int DescriptorRecordLimit = 1_000_000;
    private const ulong DescriptorByteLimit = 1UL << 30;
    private static nint _descriptorHeapTable;
    internal static readonly Guid DescriptorHeapId = new("8efb471d-616c-4f49-90f7-127bb763fa51");
    private static readonly Lock DescriptorGate = new();
    private static readonly Dictionary<string, ulong> DescriptorKeys = new(StringComparer.Ordinal);
    private static readonly Dictionary<ulong, DescriptorMetadata> DescriptorRecords = new();

    [LibraryImport("kernel32.dll", EntryPoint = "VirtualQuery")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint QueryDescriptorMemory(nint address, DescriptorMemoryInformation* information,
        nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadProcessMemory")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReadDescriptorMemory(nint process, nint address, void* output, nuint size, nuint* read);

    private static uint DescriptorStride(uint kind)
    {
        return kind switch
        {
            0 => 32,
            1 => 16,
            2 or 3 => 128,
            _ => 0
        };
    }

    private static bool IsDescriptorHeap(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _descriptorHeapTable;
    }

    private static bool DescriptorRange(ulong address, ulong size, bool writable)
    {
        if (size == 0 || address == 0 || address > (ulong)nint.MaxValue || size > (ulong)nint.MaxValue - address)
        {
            return false;
        }

        var end = address + size;

        while (address < end)
        {
            var info = default(DescriptorMemoryInformation);

            if (QueryDescriptorMemory((nint)address, &info, (nuint)sizeof(DescriptorMemoryInformation)) == 0
                || info.State != 0x1000 || (info.Protect & 0x100) != 0)
            {
                return false;
            }

            var protection = info.Protect & 0xff;

            if (writable ? protection is not (4 or 8) : protection is not (2 or 4 or 8 or 0x20 or 0x40 or 0x80))
            {
                return false;
            }

            var next = info.BaseAddress + (ulong)info.RegionSize;

            if (next <= address)
            {
                return false;
            }

            address = next;
        }

        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateDescriptorHeap(nint self, DescriptorHeapDescription* description, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null || DescriptorStride(description->Kind) == 0)
        {
            return InvalidArgument;
        }

        if (iid == null || (*iid != DescriptorHeapId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId
                            && *iid != PageableId))
        {
            return NoInterface;
        }

        var size = (ulong)DescriptorStride(description->Kind) * Math.Max(description->Count, 1U);

        if (size > DescriptorByteLimit)
        {
            return OutOfMemory;
        }

        if (output == null)
        {
            return 1;
        }

        nint owned = 0;

        DescriptorHeapObject* heap = null;

        try
        {
            // Upstream infers extended placement by exact size. ReadProcessMemory avoids an
            // unchecked read past a legacy description; the hint is not proof of ABI version.
            var placement = default(DescriptorPlacement);

            nuint read = 0;

            var placed = _newer && ReadDescriptorMemory(-1, (nint)(description + 1), &placement,
                                    (nuint)sizeof(DescriptorPlacement), &read) != 0
                                && read == (nuint)sizeof(DescriptorPlacement) && placement.Address != 0
                                && placement.Size == size;

            switch (placed)
            {
                case true when placement.Address % DescriptorStride(description->Kind) != 0
                               || !DescriptorRange(placement.Address, size, true):
                    return InvalidArgument;

                case false:
                {
                    owned = AllocateBacking(size, 64);

                    if (owned == 0)
                    {
                        return OutOfMemory;
                    }

                    break;
                }
            }

            heap = (DescriptorHeapObject*)NativeMemory.AllocZeroed((nuint)sizeof(DescriptorHeapObject));

            heap->Header.Table = _descriptorHeapTable;
            heap->Header.References = 1;
            heap->Description = *description;

            heap->Start = placed
                ? placement.Address
                : (ulong)owned;

            heap->OwnedMemory = owned;

            LiveObjects[(nint)heap] = _descriptorHeapTable;

            *output = (nint)heap;

            Console.WriteLine(
                $"[graphics] Descriptor heap: "
                + $"type={description->Kind}, "
                + $"count={description->Count}, "
                + $"stride={DescriptorStride(description->Kind)}, {(placed ? "borrowed" : "owned")} CPU backing.");

            return 0;
        }
        catch (OutOfMemoryException)
        {
            NativeMemory.Free(heap);

            NativeMemory.AlignedFree((void*)owned);

            return OutOfMemory;
        }
    }

    private static void ReleaseDescriptorHeap(nint value)
    {
        NativeMemory.AlignedFree((void*)((DescriptorHeapObject*)value)->OwnedMemory);

        foreach (var key in ResourcePrivateData.Keys)
        {
            if (key.Item1 == value)
            {
                _ = ResourcePrivateData.TryRemove(key, out _);
            }
        }

        _ = LiveObjects.TryRemove(value, out _);

        NativeMemory.Free((void*)value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DescribeDescriptorHeap(nint self, DescriptorHeapDescription* output)
    {
        if (output != null)
        {
            *output = ((DescriptorHeapObject*)self)->Description;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong DescriptorHeapStart(nint self)
    {
        return ((DescriptorHeapObject*)self)->Start;
    }

    private static void ValidateDescriptorRange(ulong address, ulong size, bool writable)
    {
        if (!DescriptorRange(address, size, writable))
        {
            Environment.FailFast("[graphics] Invalid descriptor memory range.");
        }
    }

    private static long DescriptorResourceGeneration(nint resource)
    {
        if (resource == 0)
        {
            return 0;
        }

        if (!IsResource(resource))
        {
            Environment.FailFast("[graphics] Descriptor references an unknown resource object.");
        }

        return ((ResourceObject*)resource)->Generation;
    }

    // Tokens survive raw byte copies. Non-CBV metadata is interned for the process lifetime.
    // The generation prevents recycled COM addresses from resolving to a different resource.
    // Nothing here emits a native AMD descriptor or a Windows GPU descriptor.
    private static void RecordDescriptor(uint kind, uint heapKind, ulong handle, nint resource, nint counter,
        uint* description, int words, uint* extra = null, int extraWords = 0)
    {
        if (handle == 0)
        {
            return;
        }

        var stride = DescriptorStride(heapKind);

        ValidateDescriptorRange(handle, stride, true);

        var generation = DescriptorResourceGeneration(resource);
        var counterGeneration = DescriptorResourceGeneration(counter);

        var bytes = description == null ? null : new ReadOnlySpan<byte>(description, words * sizeof(uint)).ToArray();
        var more = extra == null ? null : new ReadOnlySpan<byte>(extra, extraWords * sizeof(uint)).ToArray();

        var key =
            $"{kind}:{resource:X}:{generation}:{counter:X}:{counterGeneration}:{(bytes == null ? "-" : Convert.ToHexString(bytes))}:{(more == null ? "-" : Convert.ToHexString(more))}";

        lock (DescriptorGate)
        {
            if (!DescriptorKeys.TryGetValue(key, out var id))
            {
                if (DescriptorRecords.Count >= DescriptorRecordLimit)
                {
                    Environment.FailFast(
                        "[graphics] CPU descriptor metadata limit reached; GPU descriptor lifetime management is not implemented.");
                }

                id = (ulong)DescriptorRecords.Count + 1;

                DescriptorRecords.Add(id,
                    new DescriptorMetadata(kind, resource, generation, counter, counterGeneration, bytes, more));

                DescriptorKeys.Add(key, id);
            }

            NativeMemory.Clear((void*)handle, stride);

            Unsafe.WriteUnaligned((void*)handle, new DescriptorToken { Tag = DescriptorTag, Kind = kind, Id = id });
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateCbv(nint self, uint* description, ulong handle)
    {
        if (description == null || handle == 0)
        {
            return;
        }

        ValidateDescriptorRange(handle, 32, true);

        NativeMemory.Clear((void*)handle, 32);

        Unsafe.WriteUnaligned((void*)handle,
            new ConstantDescriptor
            {
                Tag = ConstantTag,
                Address = description[0] | ((ulong)description[1] << 32),
                Size = description[2]
            });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateSrv(nint self, nint resource, uint* description, ulong handle)
    {
        RecordDescriptor(18, 0, handle, resource, 0, description, 10);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateUav(nint self, nint resource, nint counter, uint* description, ulong handle)
    {
        RecordDescriptor(19, 0, handle, resource, counter, description, 10);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateRtv(nint self, nint resource, uint* description, ulong handle)
    {
        RecordDescriptor(20, 2, handle, resource, 0, description, 6);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateDsv(nint self, nint resource, uint* description, ulong handle)
    {
        RecordDescriptor(21, 3, handle, resource, 0, description, 6);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateSampler(nint self, uint* description, ulong handle)
    {
        if (description != null)
        {
            RecordDescriptor(22, 1, handle, 0, 0, description, 13);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreateSamplerX(nint self, uint* description, ulong handle)
    {
        if (description != null)
        {
            RecordDescriptor(61, 1, handle, 0, 0, description, 27);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreatePlacedSrv(nint self, uint* resource, uint* description, ulong handle)
    {
        if (resource != null && description != null)
        {
            RecordDescriptor(70, 0, handle, 0, 0, description, 16, resource, 14);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CreatePlacedUav(nint self, uint* resource, uint* description, ulong handle)
    {
        if (resource != null && description != null)
        {
            RecordDescriptor(71, 0, handle, 0, 0, description, 16, resource, 14);
        }
    }

    private static void CopyDescriptorBytes(ulong destination, ulong source, ulong length)
    {
        switch (length)
        {
            case 0:
                return;

            case > DescriptorByteLimit:
                Environment.FailFast("[graphics] Descriptor copy exceeds the supported size.");

                break;
        }

        ValidateDescriptorRange(source, length, false);

        ValidateDescriptorRange(destination, length, true);

        new ReadOnlySpan<byte>((void*)source, (int)length).CopyTo(new Span<byte>((void*)destination, (int)length));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyDescriptorsSimple(nint self, uint count, ulong destination, ulong source, uint kind)
    {
        var stride = DescriptorStride(kind);

        if (stride == 0)
        {
            Environment.FailFast("[graphics] Invalid descriptor copy type.");
        }

        CopyDescriptorBytes(destination, source, (ulong)stride * count);
    }

    private static ulong DescriptorTotal(uint ranges, uint* sizes)
    {
        ulong count = 0;

        for (var index = 0U; index < ranges; index++)
        {
            count += sizes == null ? 1U : sizes[index];
        }

        return count;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyDescriptors(nint self, uint destinationRanges, ulong* destinations, uint* destinationSizes,
        uint sourceRanges, ulong* sources, uint* sourceSizes, uint kind)
    {
        var stride = DescriptorStride(kind);

        if (stride == 0 || destinations == null || sources == null || destinationRanges > 65536 || sourceRanges > 65536)
        {
            Environment.FailFast("[graphics] Invalid descriptor range copy.");
        }

        var count = DescriptorTotal(sourceRanges, sourceSizes);

        if (count != DescriptorTotal(destinationRanges, destinationSizes) || count > DescriptorByteLimit / stride)
        {
            Environment.FailFast("[graphics] Descriptor range counts differ or exceed the supported size.");
        }

        if (count == 0)
        {
            return;
        }

        // Snapshot all sources before writing any destinations, including overlapping ranges.
        var length = count * stride;
        var scratch = NativeMemory.Alloc((nuint)length);

        try
        {
            var offset = 0UL;

            for (var index = 0U; index < sourceRanges; index++)
            {
                var bytes = (ulong)(sourceSizes == null ? 1U : sourceSizes[index]) * stride;

                CopyDescriptorBytes((ulong)scratch + offset, sources[index], bytes);

                offset += bytes;
            }

            offset = 0;

            for (var index = 0U; index < destinationRanges; index++)
            {
                var bytes = (ulong)(destinationSizes == null ? 1U : destinationSizes[index]) * stride;

                CopyDescriptorBytes(destinations[index], (ulong)scratch + offset, bytes);

                offset += bytes;
            }
        }
        finally
        {
            NativeMemory.Free(scratch);
        }
    }

    private static void RegisterDescriptorMethods(nint* device)
    {
        if (sizeof(DescriptorHeapDescription) != 16 || sizeof(DescriptorPlacement) != 16
                                                    || sizeof(DescriptorToken) != 16
                                                    || sizeof(DescriptorMemoryInformation) != 48)
        {
            Environment.FailFast("Descriptor ABI structure size mismatch.");
        }

        device[14] =
            (nint)(delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)
            &CreateDescriptorHeap;

        device[17] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, ulong, void>)&CreateCbv;

        device[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint*, ulong, void>)&CreateSrv;

        device[19] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, uint*, ulong, void>)&CreateUav;

        device[20] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint*, ulong, void>)&CreateRtv;

        device[21] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint*, ulong, void>)&CreateDsv;

        device[22] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, ulong, void>)&CreateSampler;

        device[23] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong*, uint*, uint, ulong*, uint*, uint, void>)
            &CopyDescriptors;

        device[24] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, ulong, uint, void>)&CopyDescriptorsSimple;

        device[61] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, ulong, void>)&CreateSamplerX;

        device[70] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, uint*, ulong, void>)&CreatePlacedSrv;

        device[71] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, uint*, ulong, void>)&CreatePlacedUav;

        var heap = ChildTable(24);

        heap[8] = (nint)(delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, void>)&DescribeDescriptorHeap;

        heap[9] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&DescriptorHeapStart;

        heap[10] = heap[9];

        _descriptorHeapTable = (nint)heap;
    }
}
