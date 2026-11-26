// SPDX-License-Identifier: GPL-3.0-or-later
// Audio heap ABI and SHAPE context layout cross-checked against
// VIctory/victory-fork crates/victory-hle/src/modules/audio.rs.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Audio.Acp.Models;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Audio.Acp;

public static unsafe partial class AcpHalModule
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private const int Failure = unchecked((int)0x80004005);
    private const int AlreadyAllocated = unchecked((int)0x8ACA0001);
    private const ulong DeviceBase = 0x10000;
    private const ulong DeviceEnd = 1UL << 32;
    private const int ContextCount = 6;
    private const int ContextHostOffset = 24;
    private const int ContextDeviceOffset = 72;
    private const int ContextDescriptionBytes = 96;

    private static readonly Lock HeapGate = new();
    private static readonly Lock ContextGate = new();
    private static readonly SortedList<nuint, AcpHalBlock> ByHost = new();
    private static readonly SortedList<uint, AcpHalBlock> ByDevice = new();
    private static readonly nuint[] ContextHosts = new nuint[ContextCount];
    private static bool _heapCreated;
    private static bool _contextsAllocated;

    public static HleModule Create()
    {
        return new HleModule("acphal", ["acphal.dll"],
        [
            new HleExport("AcpHalCreate", (nuint)(delegate* unmanaged[Stdcall]<nint*, int>)&CreateAcp),
            new HleExport("ApuCreateHeap", (nuint)(delegate* unmanaged[Stdcall]<uint, uint, int>)&CreateHeap),
            new HleExport("ApuAlloc",
                (nuint)(delegate* unmanaged[Stdcall]<void**, uint*, uint, uint, uint, int>)&Allocate),
            new HleExport("ApuFree", (nuint)(delegate* unmanaged[Stdcall]<void*, void>)&Free),
            new HleExport("ApuMapVirtualAddress", (nuint)(delegate* unmanaged[Stdcall]<void*, uint>)&MapVirtualAddress),
            new HleExport("ApuMapApuAddress", (nuint)(delegate* unmanaged[Stdcall]<uint, void*>)&MapApuAddress),
            new HleExport("ApuIsVirtualAddressValid",
                (nuint)(delegate* unmanaged[Stdcall]<void*, uint, byte>)&IsVirtualAddressValid),
            new HleExport("AcpHalAllocateShapeContexts",
                (nuint)(delegate* unmanaged[Stdcall]<byte*, int>)&AllocateShapeContexts),
            new HleExport("AcpHalReleaseShapeContexts", (nuint)(delegate* unmanaged[Stdcall]<int>)&ReleaseShapeContexts)
        ]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateHeap(uint cached, uint nonCached)
    {
        if (cached == 0)
        {
            return InvalidArgument;
        }

        lock (HeapGate)
        {
            if (_heapCreated)
            {
                return AlreadyAllocated;
            }

            _heapCreated = true;
        }

        // Sizes are advisory, matching the reference implementation. Host
        // backing is allocated on demand, not reserved or zeroed here.
        Console.Error.WriteLine(
            $"[audio] IRX-ACP-R6: APU heap initialized; cached={cached}, nonCached={nonCached} (CPU backing, advisory sizes).");

        return 0;
    }

    // Search only the sorted live intervals. Translation is O(log n),
    // without a managed allocation or a per-byte translation table.
    private static int FloorIndex<TKey>(SortedList<TKey, AcpHalBlock> index, TKey address)
        where TKey : unmanaged, IComparable<TKey>
    {
        var low = 0;
        var high = index.Count;

        while (low < high)
        {
            var middle = low + ((high - low) >> 1);

            if (index.GetKeyAtIndex(middle).CompareTo(address) <= 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low - 1;
    }

    private static ulong AlignUp(ulong value, uint alignment)
    {
        return (value + alignment - 1) & ~((ulong)alignment - 1);
    }

    // First fit over the complement of the live intervals. Free space is
    // implicit: deletion immediately merges adjacent gaps. Allocation and
    // deletion are O(n); hot-path address translation is O(log n).
    private static bool TryFindDeviceRange(uint size, uint alignment, out uint address)
    {
        var candidate = AlignUp(DeviceBase, alignment);

        for (var index = 0; index < ByDevice.Count; index++)
        {
            var block = ByDevice.GetValueAtIndex(index);

            if (candidate + size <= block.Device)
            {
                address = (uint)candidate;

                return true;
            }

            candidate = AlignUp(Math.Max(candidate, (ulong)block.Device + block.Size), alignment);
        }

        if (candidate + size <= DeviceEnd)
        {
            address = (uint)candidate;

            return true;
        }

        address = 0;

        return false;
    }

    private static int AllocateCore(void** output, uint* deviceAddress, uint size, uint alignment,
        bool contextOwned = false)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = null;

        if (deviceAddress != null)
        {
            *deviceAddress = 0;
        }

        if (size == 0 || alignment < 4 || !BitOperations.IsPow2(alignment))
        {
            return InvalidArgument;
        }

        var effectiveAlignment = Math.Max(16U, alignment);
        var roundedSize = AlignUp(size, effectiveAlignment);

        if (roundedSize > DeviceEnd - DeviceBase)
        {
            return OutOfMemory;
        }

        var memory = (void*)null;

        try
        {
            // Allocate and clear outside the index lock. No global lock is
            // held during potentially large host memory operations.
            memory = NativeMemory.AlignedAlloc((nuint)roundedSize, effectiveAlignment);

            if (memory == null)
            {
                return OutOfMemory;
            }

            NativeMemory.Clear(memory, (nuint)roundedSize);

            lock (HeapGate)
            {
                if (!TryFindDeviceRange(size, effectiveAlignment, out var address))
                {
                    return OutOfMemory;
                }

                // Reserve both metadata arrays before publishing either
                // index entry, so allocation failure cannot split the maps.
                EnsureCapacity(ByHost);

                EnsureCapacity(ByDevice);

                var block = new AcpHalBlock((nuint)memory, address, size, contextOwned);

                ByHost.Add(block.Host, block);

                ByDevice.Add(block.Device, block);

                _heapCreated = true;

                *output = memory;

                if (deviceAddress != null)
                {
                    *deviceAddress = address;
                }

                memory = null; // Ownership transferred to the two indexes.
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
        finally
        {
            if (memory != null)
            {
                NativeMemory.AlignedFree(memory);
            }
        }
    }

    private static void EnsureCapacity<TKey>(SortedList<TKey, AcpHalBlock> index) where TKey : notnull
    {
        if (index.Count == index.Capacity)
        {
            if (index.Capacity > int.MaxValue / 2)
            {
                throw new OutOfMemoryException();
            }

            index.Capacity = index.Capacity == 0 ? 16 : index.Capacity * 2;
        }
    }

    private static void FreeCore(void* memory, bool releaseContext = false)
    {
        if (memory == null)
        {
            return;
        }

        lock (HeapGate)
        {
            if (!ByHost.TryGetValue((nuint)memory, out var block) || block.ContextOwned != releaseContext)
            {
                return;
            }

            _ = ByHost.Remove(block.Host);

            _ = ByDevice.Remove(block.Device);
        }

        NativeMemory.AlignedFree(memory);
    }

    private static uint MapVirtualAddressCore(void* memory)
    {
        var address = (nuint)memory;

        lock (HeapGate)
        {
            var index = FloorIndex(ByHost, address);

            if (index < 0)
            {
                return 0;
            }

            var block = ByHost.GetValueAtIndex(index);
            var offset = address - block.Host;

            return offset < block.Size ? block.Device + (uint)offset : 0;
        }
    }

    private static void* MapApuAddressCore(uint address)
    {
        lock (HeapGate)
        {
            var index = FloorIndex(ByDevice, address);

            if (index < 0)
            {
                return null;
            }

            var block = ByDevice.GetValueAtIndex(index);
            var offset = address - block.Device;

            return offset < block.Size ? (void*)(block.Host + offset) : null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Allocate(void** output, uint* deviceAddress, uint size, uint alignment, uint flags)
    {
        // Cache flags do not change host memory policy in this revision.
        return AllocateCore(output, deviceAddress, size, alignment);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Free(void* memory)
    {
        FreeCore(memory);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint MapVirtualAddress(void* memory)
    {
        return MapVirtualAddressCore(memory);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* MapApuAddress(uint address)
    {
        return MapApuAddressCore(address);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte IsVirtualAddressValid(void* memory, uint alignment)
    {
        // Rust bool / console ABI is a one-byte return, not Win32 BOOL.
        var address = MapVirtualAddressCore(memory);

        return address != 0 && (alignment == 0 || address % alignment == 0) ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AllocateShapeContexts(byte* description)
    {
        if (description == null)
        {
            return InvalidArgument;
        }

        lock (ContextGate)
        {
            if (_contextsAllocated)
            {
                return Failure;
            }

            // Packed ABI: six u32 counts, six x64 pointers, six u32 APU
            // addresses. The EQ/filter/PCM strides are provisional upstream
            // estimates, not verified hardware layouts. No DSP is executed.
            ReadOnlySpan<uint> strides = [32, 256, 256, 16, 64, 256];

            Span<nuint> hosts = stackalloc nuint[ContextCount];

            Span<uint> devices = stackalloc uint[ContextCount];

            hosts.Clear();

            devices.Clear();

            // Zero only the output area, keeping input counts intact.
            new Span<byte>(description + ContextHostOffset, ContextDescriptionBytes - ContextHostOffset).Clear();

            for (var index = 0; index < ContextCount; index++)
            {
                var count = Unsafe.ReadUnaligned<uint>(description + index * sizeof(uint));

                if (count == 0)
                {
                    continue;
                }

                var bytes = (ulong)count * strides[index];

                if (bytes > uint.MaxValue)
                {
                    FreeContexts(hosts);

                    return OutOfMemory;
                }

                var memory = (void*)null;
                var device = 0U;
                var result = AllocateCore(&memory, &device, (uint)bytes, 256, true);

                if (result < 0)
                {
                    FreeContexts(hosts);

                    return result;
                }

                hosts[index] = (nuint)memory;

                devices[index] = device;
            }

            for (var index = 0; index < ContextCount; index++)
            {
                Unsafe.WriteUnaligned(description + ContextHostOffset + index * sizeof(ulong), (ulong)hosts[index]);

                Unsafe.WriteUnaligned(description + ContextDeviceOffset + index * sizeof(uint), devices[index]);
            }

            hosts.CopyTo(ContextHosts);

            _contextsAllocated = true;
        }

        Console.Error.WriteLine(
            "[audio] SHAPE context storage allocated (CPU only; no decoder or audio command processing).");

        return 0;
    }

    private static void FreeContexts(ReadOnlySpan<nuint> hosts)
    {
        foreach (var host in hosts)
        {
            FreeCore((void*)host, true);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ReleaseShapeContexts()
    {
        lock (ContextGate)
        {
            if (!_contextsAllocated)
            {
                return Failure;
            }

            FreeContexts(ContextHosts);

            Array.Clear(ContextHosts);

            _contextsAllocated = false;
        }

        return 0;
    }
}
