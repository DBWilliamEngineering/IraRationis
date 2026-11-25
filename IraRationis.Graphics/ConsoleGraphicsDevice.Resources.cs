// SPDX-License-Identifier: GPL-3.0-or-later
// Console resource ABI adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.

using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Textures;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const ulong ResourcePage = 65536;
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private static nint _resourceTable, _heapTable;
    private static readonly Guid ResourceId = new("696442be-a72e-4059-bc79-5b5c98040fad");
    private static readonly Guid HeapId = new("6b3b2502-6e51-45b3-90ee-9884265e8df3");
    private static readonly ConcurrentDictionary<(nint, Guid), byte[]> ResourcePrivateData = new();
    private static readonly ConcurrentDictionary<nuint, (ulong Address, uint Pages)> PagePools = new();
    private static long _nextPool;
    private static long _nextResourceGeneration;

    private static readonly bool TraceResourceAllocation =
        Environment.GetEnvironmentVariable("IRX_TRACE_RESOURCE_ALLOCATION") == "1";

    private static bool IsResource(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _resourceTable;
    }

    private static bool IsHeap(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _heapTable;
    }

    private static void ReleaseResourceStorage(nint value)
    {
        if (IsResource(value))
        {
            var resource = (ResourceObject*)value;
            ReleaseHostResource(resource);
            if (resource->OwnedMemory != 0)
            {
                FreeCommittedBacking(resource->OwnedMemory, resource->FreeHook, resource->AllocationAttributes);
            }

            if (resource->OwningHeap != 0)
            {
                _ = Drop(resource->OwningHeap);
            }
        }
        else
        {
            NativeMemory.AlignedFree((void*)((HeapObject*)value)->Memory);
        }

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

    private static bool ResourceFootprint(ResourceDescription description, out AllocationInfo info)
    {
        info = new AllocationInfo { Size = ulong.MaxValue };
        if (description.Dimension != 1)
        {
            if (!TextureFootprintCalculator.TryCalculate(description, out var size, out var textureAlignment))
            {
                return false;
            }

            info = new AllocationInfo { Size = size, Alignment = textureAlignment };

            return true;
        }

        if (description.Width == 0 || description.SampleCount > 1
                                   || (description.Alignment != 0 && !BitOperations.IsPow2(description.Alignment)))
        {
            return false;
        }

        var alignment = Math.Max(ResourcePage, description.Alignment);
        if (!BitOperations.IsPow2(alignment) || description.Width > ulong.MaxValue - (alignment - 1))
        {
            return false;
        }

        info = new AllocationInfo
            { Size = (description.Width + alignment - 1) & ~(alignment - 1), Alignment = alignment };

        return true;
    }

    private static nint AllocateBacking(ulong size, ulong alignment)
    {
        if (size == 0 || size > (ulong)nint.MaxValue || !BitOperations.IsPow2(alignment)
            || alignment > (ulong)nint.MaxValue || size > ulong.MaxValue - (alignment - 1))
        {
            return 0;
        }

        var rounded = (size + alignment - 1) & ~(alignment - 1);
        if (rounded > (ulong)nint.MaxValue)
        {
            return 0;
        }

        var memory = NativeMemory.AlignedAlloc((nuint)rounded, (nuint)alignment);
        if (memory != null)
        {
            NativeMemory.Clear(memory, (nuint)rounded);
        }

        return (nint)memory;
    }

    private static bool ValidResourceInterface(Guid* iid)
    {
        return iid != null && (*iid == ResourceId || *iid == UnknownId || *iid == ObjectId || *iid == ChildId
                               || *iid == PageableId);
    }

    private static int NewResource(ResourceDescription description, HeapProperties properties, uint flags,
        ulong address, nint heap, nint owned, Guid* iid, nint* output, nint freeHook = 0, ulong attributes = 0)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (!ValidResourceInterface(iid) || output == null)
        {
            if (owned != 0)
            {
                FreeCommittedBacking(owned, freeHook, attributes);
            }

            return ValidResourceInterface(iid) ? 1 : NoInterface;
        }

        ResourceObject* value = null;
        try
        {
            value = (ResourceObject*)NativeMemory.AllocZeroed((nuint)sizeof(ResourceObject));
            value->Header.Table = _resourceTable;
            value->Header.References = 1;
            value->Description = description;
            value->Properties = properties;
            value->HeapFlags = flags;
            value->Address = address;
            value->Generation = Interlocked.Increment(ref _nextResourceGeneration);
            value->OwningHeap = heap;
            value->OwnedMemory = owned;
            value->FreeHook = freeHook;
            value->AllocationAttributes = attributes;
            LiveObjects[(nint)value] = _resourceTable;
            if (heap != 0)
            {
                _ = Retain(heap);
            }

            *output = (nint)value;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            NativeMemory.Free(value);
            if (owned != 0)
            {
                FreeCommittedBacking(owned, freeHook, attributes);
            }

            return OutOfMemory;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateResourceAtAddress(nint self, ulong address, ResourceDescription* description, uint layout,
        nint clear, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null || address == 0 || address > (ulong)nint.MaxValue
            || description->Dimension is < 1 or > 4 || description->Width == 0)
        {
            return InvalidArgument;
        }

        if (!ResourceFootprint(*description, out var info) || info.Size > (ulong)nint.MaxValue - address)
        {
            return InvalidArgument;
        }

        // Borrow the existing title address. Never copy, free or assume its pages are committed.
        // This establishes the console resource identity only; no host shader or GPU address translation yet.
        var properties = new HeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };
        var hr = NewResource(*description, properties, 0, address, 0, 0, iid, output);
        if (hr == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] Resource view: dimension={description->Dimension}, width={description->Width}, layout=0x{description->Layout:X}, address=0x{address:X} (CPU backing only).");
        }

        return hr;
    }

    private static int CreateCommittedBacking(string operation, HeapProperties* properties, uint flags,
        ResourceDescription* description, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (properties == null || description == null)
        {
            return ReportCommittedResult(operation, InvalidArgument, 0);
        }

        if (TraceResourceAllocation)
        {
            Console.Error.WriteLine(
                $"[graphics] {operation}: dimension={description->Dimension}, width={description->Width}, height={description->Height}, heap={properties->Kind}, flags=0x{flags:X}, resourceFlags=0x{description->Flags:X}.");
        }

        if (!ResourceFootprint(*description, out var info))
        {
            return ReportCommittedResult(operation, NotImplemented, 0);
        }

        if (!ValidResourceInterface(iid))
        {
            if (iid != null)
            {
                Console.Error.WriteLine($"[graphics] {operation}: unsupported IID {*iid}.");
            }

            return ReportCommittedResult(operation, NoInterface, 0);
        }

        if (output == null)
        {
            return ReportCommittedResult(operation, 1, 0);
        }

        try
        {
            var hooks = _resourceAllocator?.Invoke() ?? default;
            if (hooks.Allocate == 0 != (hooks.Free == 0))
            {
                Console.Error.WriteLine(
                    $"[graphics] {operation}: incomplete allocation hook pair; refusing mixed allocator ownership.");

                return ReportCommittedResult(operation, NotImplemented, 0);
            }

            if (info.Size == 0 || info.Size > (ulong)nint.MaxValue || !BitOperations.IsPow2(info.Alignment)
                || info.Alignment > 1UL << 31)
            {
                return ReportCommittedResult(operation, InvalidArgument, 0);
            }

            var attributes = ResourceAllocationAttributes(info.Size, info.Alignment, properties->Kind);
            if (TraceResourceAllocation)
            {
                Console.Error.WriteLine(
                    $"[graphics] {operation}: size={info.Size}, alignment={info.Alignment}, attributes=0x{attributes:X}, owner={(hooks.Allocate == 0 ? "IRX" : "registered allocator")}.");
            }

            var memory = hooks.Allocate == 0
                ? AllocateBacking(info.Size, info.Alignment)
                : (nint)((delegate* unmanaged[Stdcall]<nuint, ulong, void*>)hooks.Allocate)((nuint)info.Size,
                    attributes);
            if (memory == 0)
            {
                return ReportCommittedResult(operation, OutOfMemory, 0);
            }

            if ((ulong)memory % info.Alignment != 0)
            {
                Console.Error.WriteLine($"[graphics] {operation}: misaligned allocation 0x{(nuint)memory:X}.");
                FreeCommittedBacking(memory, hooks.Free, attributes);

                return ReportCommittedResult(operation, InvalidArgument, 0);
            }

            if (hooks.Allocate != 0)
            {
                NativeMemory.Clear((void*)memory, (nuint)info.Size);
            }

            var hr = NewResource(*description, *properties, flags, (ulong)memory, 0, memory, iid, output, hooks.Free,
                attributes);

            return ReportCommittedResult(operation, hr, hr == 0 ? *output : 0);
        }
        catch (OutOfMemoryException)
        {
            return ReportCommittedResult(operation, OutOfMemory, 0);
        }
    }

    private static int ReportCommittedResult(string operation, int hr, nint resource)
    {
        if (TraceResourceAllocation || hr < 0)
        {
            Console.Error.WriteLine($"[graphics] {operation}: hr=0x{hr:X8}, resource=0x{(nuint)resource:X}.");
        }

        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateCommittedResource(nint self, HeapProperties* properties, uint flags,
        ResourceDescription* description, uint state, nint clear, Guid* iid, nint* output)
    {
        return CreateCommittedBacking("CreateCommittedResource", properties, flags, description, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateCommittedResource3(nint self, HeapProperties* properties, uint flags,
        ResourceDescription* description, uint layout, nint clear, nint session, uint castableCount, uint* castable,
        Guid* iid, nint* output)
    {
        if (session != 0 || castableCount != 0)
        {
            if (output != null)
            {
                *output = 0;
            }

            Console.Error.WriteLine(
                $"[graphics] CreateCommittedResource3: unsupported session=0x{(nuint)session:X}, castableCount={castableCount}.");

            return ReportCommittedResult("CreateCommittedResource3", NotImplemented, 0);
        }

        return CreateCommittedBacking("CreateCommittedResource3", properties, flags, description, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateResourceHeap(nint self, HeapDescription* description, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null || description->Size == 0
                                || (description->Alignment != 0 && !BitOperations.IsPow2(description->Alignment)))
        {
            return InvalidArgument;
        }

        if (iid == null || (*iid != HeapId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId
                            && *iid != PageableId))
        {
            return NoInterface;
        }

        if (output == null)
        {
            return 1;
        }

        nint memory = 0;
        HeapObject* value = null;
        try
        {
            memory = AllocateBacking(description->Size, Math.Max(ResourcePage, description->Alignment));
            if (memory == 0)
            {
                return OutOfMemory;
            }

            value = (HeapObject*)NativeMemory.AllocZeroed((nuint)sizeof(HeapObject));
            value->Header.Table = _heapTable;
            value->Header.References = 1;
            value->Description = *description;
            value->Memory = memory;
            LiveObjects[(nint)value] = _heapTable;
            *output = (nint)value;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            NativeMemory.Free(value);
            NativeMemory.AlignedFree((void*)memory);

            return OutOfMemory;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreatePlacedResource(nint self, nint heap, ulong offset, ResourceDescription* description,
        uint state, nint clear, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (!IsHeap(heap) || description == null)
        {
            return InvalidArgument;
        }

        if (!ResourceFootprint(*description, out var info))
        {
            return NotImplemented;
        }

        var backing = (HeapObject*)heap;
        if (offset % info.Alignment != 0 || (ulong)backing->Memory % info.Alignment != 0
                                         || offset > backing->Description.Size
                                         || info.Size > backing->Description.Size - offset)
        {
            return InvalidArgument;
        }

        return NewResource(*description, backing->Description.Properties, backing->Description.Flags,
            (ulong)backing->Memory + offset, heap, 0, iid, output);
    }

    private static void ReportResourceAllocation(uint index, uint count, ResourceDescription description, string reason)
    {
        Console.Error.WriteLine(
            $"[graphics] GetResourceAllocationInfo [{index + 1}/{count}] {reason}: " +
            $"dimension={description.Dimension}, width={description.Width}, height={description.Height}, " +
            $"depthOrArray={description.DepthOrArraySize}, mips={description.MipLevels}, " +
            $"format={description.Format}, samples={description.SampleCount}, quality={description.SampleQuality}, " +
            $"layout=0x{description.Layout:X}, flags=0x{description.Flags:X}, alignment={description.Alignment}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static AllocationInfo* ResourceAllocationInfo(AllocationInfo* output, nint self, uint visible, uint count,
        ResourceDescription* descriptions)
    {
        if (output == null)
        {
            Console.Error.WriteLine("[graphics] GetResourceAllocationInfo: null return buffer.");

            return null;
        }

        *output = new AllocationInfo { Size = ulong.MaxValue };
        if (descriptions == null || count > 65536)
        {
            Console.Error.WriteLine(
                $"[graphics] GetResourceAllocationInfo rejected: count={count}, descriptions=0x{(nuint)descriptions:X}, visible=0x{visible:X}.");

            return output;
        }

        var size = 0UL;
        var alignment = ResourcePage;
        for (var index = 0U; index < count; index++)
        {
            var description = descriptions[index];
            if (TraceResourceAllocation)
            {
                ReportResourceAllocation(index, count, description, "request");
            }

            if (!ResourceFootprint(description, out var info))
            {
                var reason = description.Dimension != 1
                    ? "unsupported texture footprint"
                    : "invalid or unsupported buffer footprint";
                ReportResourceAllocation(index, count, description, reason);
                Console.Error.WriteLine(
                    $"[graphics] GetResourceAllocationInfo returning size=0x{output->Size:X16}, alignment={output->Alignment}.");

                return output;
            }

            if (size > ulong.MaxValue - (info.Alignment - 1))
            {
                ReportResourceAllocation(index, count, description, "offset alignment overflow");

                return output;
            }

            size = (size + info.Alignment - 1) & ~(info.Alignment - 1);
            if (size > ulong.MaxValue - info.Size)
            {
                ReportResourceAllocation(index, count, description, "size overflow");

                return output;
            }

            size += info.Size;
            alignment = Math.Max(alignment, info.Alignment);
        }

        *output = new AllocationInfo { Size = size, Alignment = alignment };
        if (TraceResourceAllocation || output->Size > uint.MaxValue)
        {
            Console.Error.WriteLine(
                $"[graphics] GetResourceAllocationInfo result: count={count}, size={output->Size} (0x{output->Size:X16}), alignment={output->Alignment}.");
        }

        return output;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterPagePool(nint self, ulong address, uint pages, nuint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = 0;
        if (address == 0 || address % ResourcePage != 0 || pages == 0 || address > (ulong)nint.MaxValue
            || pages * ResourcePage > (ulong)nint.MaxValue - address)
        {
            return InvalidArgument;
        }

        try
        {
            var handle = (nuint)Interlocked.Increment(ref _nextPool);
            PagePools[handle] = (address, pages);
            *output = handle;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int UnregisterPagePool(nint self, nuint handle)
    {
        return PagePools.TryRemove(handle, out _) ? 0 : InvalidArgument;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MapResource(nint self, uint subresource, nint range, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        // Only subresource zero has a defined CPU address in this partial implementation.
        if (subresource != 0)
        {
            return NotImplemented;
        }

        if (output != null)
        {
            *output = (nint)((ResourceObject*)self)->Address;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void UnmapResource(nint self, uint subresource, nint range)
    {
        // The borrowed or owned CPU mapping stays live until its actual owner releases it.
        // No GPU visibility is implied by this operation.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DescribeResource(nint self, ResourceDescription* output)
    {
        if (output != null)
        {
            *output = ((ResourceObject*)self)->Description;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong ResourceAddress(nint self)
    {
        return ((ResourceObject*)self)->Address;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ResourceHeapProperties(nint self, HeapProperties* properties, uint* flags)
    {
        var resource = (ResourceObject*)self;
        if (properties != null)
        {
            *properties = resource->Properties;
        }

        if (flags != null)
        {
            *flags = resource->HeapFlags;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DescribeHeap(nint self, HeapDescription* output)
    {
        if (output != null)
        {
            *output = ((HeapObject*)self)->Description;
        }
    }

    private static int GetResourcePrivate(nint self, Guid* id, uint* size, void* data)
    {
        if (id == null || size == null)
        {
            return InvalidArgument;
        }

        if (!ResourcePrivateData.TryGetValue((self, *id), out var value))
        {
            *size = 0;

            return DxgiNotFound;
        }

        var available = *size;
        *size = (uint)value.Length;
        if (data == null)
        {
            return 0;
        }

        if (available < value.Length)
        {
            return DxgiMoreData;
        }

        value.AsSpan().CopyTo(new Span<byte>(data, value.Length));

        return 0;
    }

    private static int SetResourcePrivate(nint self, Guid* id, uint size, void* data)
    {
        if (id == null || size > int.MaxValue)
        {
            return InvalidArgument;
        }

        try
        {
            if (data == null)
            {
                _ = ResourcePrivateData.TryRemove((self, *id), out _);
            }
            else
            {
                ResourcePrivateData[(self, *id)] = new ReadOnlySpan<byte>(data, (int)size).ToArray();
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
    }

    private static void RegisterResourceMethods(nint* device)
    {
        if (sizeof(ResourceDescription) != 56 || sizeof(HeapDescription) != 48 || sizeof(AllocationInfo) != 16)
        {
            Environment.FailFast("Resource ABI structure size mismatch.");
        }

        device[25] =
            (nint)(delegate* unmanaged[Stdcall]<AllocationInfo*, nint, uint, uint, ResourceDescription*, AllocationInfo*
                >)&ResourceAllocationInfo;
        device[27] =
            (nint)(delegate* unmanaged[Stdcall]<nint, HeapProperties*, uint, ResourceDescription*, uint, nint, Guid*,
                nint*, int>)&CreateCommittedResource;
        device[28] = (nint)(delegate* unmanaged[Stdcall]<nint, HeapDescription*, Guid*, nint*, int>)&CreateResourceHeap;
        device[29] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)
            &CreatePlacedResource;
        device[74] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, nuint*, int>)&RegisterPagePool;
        device[75] = (nint)(delegate* unmanaged[Stdcall]<nint, nuint, int>)&UnregisterPagePool;
        device[76] = device[27];
        device[127] =
            (nint)(delegate* unmanaged[Stdcall]<nint, HeapProperties*, uint, ResourceDescription*, uint, nint, nint,
                uint, uint*, Guid*, nint*, int>)&CreateCommittedResource3;
        device[133] =
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)
            &CreateResourceAtAddress;
        var resource = ChildTable(32);
        resource[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)&MapResource;
        resource[9] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, void>)&UnmapResource;
        resource[10] = (nint)(delegate* unmanaged[Stdcall]<nint, ResourceDescription*, void>)&DescribeResource;
        resource[11] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&ResourceAddress;
        resource[14] = (nint)(delegate* unmanaged[Stdcall]<nint, HeapProperties*, uint*, int>)&ResourceHeapProperties;
        _resourceTable = (nint)resource;
        var heap = ChildTable(24);
        heap[8] = (nint)(delegate* unmanaged[Stdcall]<nint, HeapDescription*, void>)&DescribeHeap;
        _heapTable = (nint)heap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ResourceDescription
    {
        public uint Dimension;
        public ulong Alignment;
        public ulong Width;
        public uint Height;
        public ushort DepthOrArraySize;
        public ushort MipLevels;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Layout;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapDescription
    {
        public ulong Size;
        public HeapProperties Properties;
        public ulong Alignment;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceObject
    {
        public Header Header;
        public ResourceDescription Description;
        public HeapProperties Properties;
        public uint HeapFlags;
        public ulong Address;
        public nint OwningHeap;
        public nint OwnedMemory;
        public long Generation;
        public nint FreeHook;
        public ulong AllocationAttributes;
        public nint HostResource;
        public ulong HostCharge;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapObject
    {
        public Header Header;
        public HeapDescription Description;
        public nint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AllocationInfo
    {
        public ulong Size;
        public ulong Alignment;
    }
}
