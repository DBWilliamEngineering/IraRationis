// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const ulong MaximumHostResourceBytes = 256UL << 20;
    private const ulong HostResourceBudget = 512UL << 20;
    private const uint CopyDestination = 0x400;
    private const uint CopySource = 0x800;
    private const uint GenericRead = 0xAC3;
    private static readonly Lock HostResourceGate = new();
    private static ulong _hostResourceBytes;

    private static int _hostTargetsReported;

    private static ResourceDescription HostBufferDescription(ulong bytes)
    {
        return new ResourceDescription
        {
            Dimension = 1, Width = bytes, Height = 1, DepthOrArraySize = 1,
            MipLevels = 1, SampleCount = 1, Layout = 1
        };
    }

    private static int CreateHostResource(ResourceDescription description, uint heapKind, uint state, nint* output)
    {
        // PC heap properties and resource description have the same sizes, but console swizzles/flags do not.
        var properties = new HeapProperties { Kind = heapKind, CreationNodes = 1, VisibleNodes = 1 };
        var iid = ResourceId;
        var device = (nint)_host;
        *output = 0;

        return ((delegate* unmanaged[Stdcall]<nint, HeapProperties*, uint, ResourceDescription*, uint, void*, Guid*,
                nint*, int>)Slot(device, 27))
            (device, &properties, 0, &description, state, null, &iid, output);
    }

    // A real DEFAULT-heap allocation. The console address and CPU backing remain authoritative.
    // No implicit upload, state transition, or alias coherence is promised by allocation alone.
    private static int MaterializeHostResource(nint value, out nint host)
    {
        host = 0;
        lock (HostResourceGate)
        {
            if (_host == null || !IsResource(value))
            {
                return InvalidArgument;
            }

            var resource = (ResourceObject*)value;
            if (resource->HostResource != 0)
            {
                host = resource->HostResource;

                return 0;
            }

            var description = resource->Description;
            if (!ResourceFootprint(description, out var footprint) || footprint.Size == 0 ||
                footprint.Size > MaximumHostResourceBytes || footprint.Size > HostResourceBudget - _hostResourceBytes)
            {
                return NotImplemented;
            }

            if (description.Dimension == 1)
            {
                description = HostBufferDescription(description.Width);
                // Buffer UAV access is the only supported host flag for this initial path.
                description.Flags = resource->Description.Flags & 4U;
            }
            else
            {
                if (description.Dimension is < 2 or > 4 || description.SampleCount != 1)
                {
                    return NotImplemented;
                }

                description.Alignment = 0;
                description.Layout = 0; // Host-optimal layout, not console swizzle.
                description.Flags &= 0xFU; // RT, DS, UAV, deny SRV; never pass console-only flags.
            }

            nint created = 0;
            var hr = CreateHostResource(description, 1, 0, &created);
            if (hr < 0 || created == 0)
            {
                return hr < 0 ? hr : NotImplemented;
            }

            resource->HostResource = created;
            resource->HostCharge = footprint.Size;
            _hostResourceBytes += footprint.Size;
            host = created;

            return 0;
        }
    }

    // Resolve our descriptor token, not a host CPU descriptor handle. No OM binding yet.
    private static void MaterializeBoundTarget(RecordedCommandState state, ulong handle, uint kind)
    {
        if (handle == 0)
        {
            return;
        }

        DescriptorToken token = default;
        nuint transferred = 0;
        if (ReadProcessMemory(-1, (void*)(nuint)handle, &token, (nuint)sizeof(DescriptorToken), &transferred) == 0 ||
            transferred != (nuint)sizeof(DescriptorToken) || token.Tag != DescriptorTag || token.Kind != kind)
        {
            return;
        }

        DescriptorMetadata? record;
        lock (DescriptorGate)
        {
            _ = DescriptorRecords.TryGetValue(token.Id, out record);
        }

        if (record == null || !IsResource(record.Resource) ||
            ((ResourceObject*)record.Resource)->Generation != record.ResourceGeneration)
        {
            return;
        }

        state.RetainObject(record.Resource);
        var hr = MaterializeHostResource(record.Resource, out _);
        var reported = Interlocked.Increment(ref _hostTargetsReported);
        if (reported <= 4 || (reported & (reported - 1)) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] Target host materialization #{reported}: HRESULT=0x{hr:X8}; allocation only, no host view binding or guest data upload.");
        }
    }

    private static void ReleaseHostResource(ResourceObject* resource)
    {
        lock (HostResourceGate)
        {
            if (resource->HostResource != 0)
            {
                _ = HostRelease(resource->HostResource);
                _hostResourceBytes -= resource->HostCharge;
                resource->HostResource = 0;
                resource->HostCharge = 0;
            }
        }
    }

    private static void CheckHostResult(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{operation}: 0x{hr:X8}");
        }
    }

    private static void TransitionHostResource(nint list, nint resource, uint before, uint after)
    {
        var barrier = new HostTransition
            { Resource = resource, Subresource = uint.MaxValue, Before = before, After = after };
        ((delegate* unmanaged[Stdcall]<nint, uint, HostTransition*, void>)Slot(list, 26))(list, 1, &barrier);
    }

    // Test uses a real guest resource, DEFAULT memory, an upload heap and a readback heap.
    // It is intentionally separate from guest execution until VUM synchronization is implemented.
    internal static void VerifyHostResourceAbi(nint device, nint queue, nint allocator)
    {
        const int bytes = 4096;
        var data = NativeMemory.AlignedAlloc((nuint)ResourcePage, (nuint)ResourcePage);
        nint guest = 0, texture = 0, upload = 0, readback = 0, list = 0;
        var description = HostBufferDescription(bytes);
        var iid = ResourceId;
        var listId = HostListId;
        var hostDevice = (nint)_host;
        var hostQueue = Host(queue);
        try
        {
            for (var i = 0; i < bytes; i++)
            {
                ((byte*)data)[i] = (byte)(i * 17 + 3);
            }

            var create =
                (delegate* unmanaged[Stdcall]<nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(
                    device, 133);
            CheckHostResult(create(device, (ulong)data, &description, 0, 0, &iid, &guest), "Guest buffer");
            CheckHostResult(MaterializeHostResource(guest, out var local), "DEFAULT resource");
            CheckHostResult(MaterializeHostResource(guest, out var again), "Repeated materialization");
            if (local == 0 || local != again || ((ResourceObject*)guest)->Address != (ulong)data)
            {
                throw new InvalidOperationException("Host resource identity or guest address changed.");
            }

            var textureDescription = new ResourceDescription
            {
                Dimension = 3, Width = 64, Height = 64, DepthOrArraySize = 1,
                MipLevels = 1, Format = 28, SampleCount = 1, Flags = 1
            };
            CheckHostResult(create(device, (ulong)data, &textureDescription, 0, 0, &iid, &texture), "Guest texture");
            CheckHostResult(MaterializeHostResource(texture, out var hostTexture), "DEFAULT texture allocation");
            if (hostTexture == 0)
            {
                throw new InvalidOperationException("Host texture is null.");
            }

            CheckHostResult(CreateHostResource(description, 2, GenericRead, &upload), "UPLOAD resource");
            CheckHostResult(CreateHostResource(description, 3, CopyDestination, &readback), "READBACK resource");
            var noRead = default(HostRange);
            void* mapped = null;
            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload, 8))(upload, 0, &noRead,
                    &mapped), "Map upload");
            new ReadOnlySpan<byte>(data, bytes).CopyTo(new Span<byte>(mapped, bytes));
            var written = new HostRange { End = bytes };
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, 9))(upload, 0, &written);
            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(hostDevice, 12))
                (hostDevice, 0, 0, Host(allocator), 0, &listId, &list), "Host copy list");
            var copy = (delegate* unmanaged[Stdcall]<nint, nint, ulong, nint, ulong, ulong, void>)Slot(list, 15);
            TransitionHostResource(list, local, 0, CopyDestination);
            copy(list, local, 0, upload, 0, bytes);
            TransitionHostResource(list, local, CopyDestination, CopySource);
            copy(list, readback, 0, local, 0, bytes);
            TransitionHostResource(list, local, CopySource, 0);
            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, 9))(list), "Close host copy list");
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(hostQueue, 10))(hostQueue, 1, &list);
            WaitForHostCompletion(hostQueue);
            mapped = null;
            var read = new HostRange { End = bytes };
            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8))(readback, 0,
                    &read, &mapped), "Map readback");
            var same = new ReadOnlySpan<byte>(mapped, bytes).SequenceEqual(new ReadOnlySpan<byte>(data, bytes));
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9))(readback, 0, &noRead);
            if (!same)
            {
                throw new InvalidOperationException("GPU resource round-trip bytes differ.");
            }

            Console.WriteLine("[graphics-probe] DEFAULT/UPLOAD/READBACK GPU resource round trip passed (4096 bytes).");
        }
        finally
        {
            if (list != 0)
            {
                _ = HostRelease(list);
            }

            if (upload != 0)
            {
                _ = HostRelease(upload);
            }

            if (readback != 0)
            {
                _ = HostRelease(readback);
            }

            if (guest != 0)
            {
                _ = Drop(guest);
            }

            if (texture != 0)
            {
                _ = Drop(texture);
            }

            NativeMemory.AlignedFree(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HostRange
    {
        public nuint Begin;
        public nuint End;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct HostTransition
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public nint Resource;
        [FieldOffset(16)] public uint Subresource;
        [FieldOffset(20)] public uint Before;
        [FieldOffset(24)] public uint After;
    }
}
