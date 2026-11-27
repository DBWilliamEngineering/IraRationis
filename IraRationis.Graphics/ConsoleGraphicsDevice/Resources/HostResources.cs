// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const ulong MaximumHostResourceBytes = 256UL << 20;
    private const ulong HostResourceBudget = 512UL << 20;
    private const int HostCreateCommittedResourceSlot = 27;
    private const int HostResourceBarrierSlot = 26;
    private const int HostCreateCommandListSlot = 12;
    private const int HostCloseCommandListSlot = 9;
    private const int HostCopyBufferRegionSlot = 15;
    private const int HostExecuteCommandListsSlot = 10;
    private const int HostMapResourceSlot = 8;
    private const int HostUnmapResourceSlot = 9;
    private const uint PrimaryNodeMask = 1;
    private const uint HostTextureResourceFlags = (uint)(D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET |
        D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL |
        D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS |
        D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_DENY_SHADER_RESOURCE);

    //TODO: Damon William -> Replace the fixed budget with measured DXGI headroom after alias coherence and fence-safe eviction exist.
    internal const uint CopyDestination = (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
    internal const uint CopySource = (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE;
    internal const uint GenericRead = (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_GENERIC_READ;
    private static readonly Lock HostResourceGate = new();
    private static ulong _hostResourceBytes;

    private static int _hostTargetsReported;

    internal static ResourceDescription HostBufferDescription(ulong bytes)
    {
        return new ResourceDescription
        {
            Dimension = (uint)D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Width = bytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleCount = 1,
            Layout = (uint)D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR
        };
    }

    internal static int CreateHostResource(ResourceDescription description, uint heapKind, uint state, nint* output)
    {
        // PC heap properties and resource description have the same sizes, but console swizzles/flags do not.
        var properties =
            new GraphicsHeapProperties { Kind = heapKind, CreationNodes = PrimaryNodeMask, VisibleNodes = PrimaryNodeMask };

        var iid = ResourceId;
        var device = (nint)_host;

        *output = 0;

        return ((delegate* unmanaged[Stdcall]<nint, GraphicsHeapProperties*, uint, ResourceDescription*, uint, void*,
                Guid*,
                nint*, int>)Slot(device, HostCreateCommittedResourceSlot))
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

            if (description.Dimension == (uint)D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER)
            {
                description = HostBufferDescription(description.Width);
                // Buffer UAV access is the only supported host flag for this initial path.
                description.Flags = resource->Description.Flags &
                                    (uint)D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
            }
            else
            {
                if (description.Dimension is < (uint)D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE1D
                    or > (uint)D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE3D || description.SampleCount != 1)
                {
                    return NotImplemented;
                }

                description.Format = HostTextureViewFormats.BackingFormat(description);
                description.Alignment = 0;

                description.Layout = (uint)D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN;
                description.Flags &= HostTextureResourceFlags;
            }

            nint created = 0;

            var resultCode = CreateHostResource(description, (uint)D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
                (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON, &created);

            if (resultCode < 0 || created == 0)
            {
                return resultCode < 0 ? resultCode : NotImplemented;
            }

            ReserveGpuWritableBacking(resource, footprint.Size);

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

        var resultCode = MaterializeHostResource(record.Resource, out _);
        var reported = Interlocked.Increment(ref _hostTargetsReported);

        if (reported <= 4 || (reported & (reported - 1)) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] Target host materialization #{reported}: HRESULT=0x{resultCode:X8}; allocation only, no host view binding or guest data upload.");
        }
    }

    private static void ReleaseHostResource(ResourceObject* resource)
    {
        lock (HostResourceGate)
        {
            if (resource->HostResource == 0)
            {
                return;
            }

            _ = HostRelease(resource->HostResource);

            _hostResourceBytes -= resource->HostCharge;

            resource->HostResource = 0;
            resource->HostCharge = 0;
        }
    }

    internal static void CheckHostResult(int resultCode, string operation)
    {
        if (resultCode < 0)
        {
            throw new InvalidOperationException($"{operation}: 0x{resultCode:X8}");
        }
    }

    internal static void TransitionHostResource(nint list, nint resource, uint before, uint after,
        uint subresource = uint.MaxValue)
    {
        var barrier = new HostTransition
        {
            Resource = resource,
            Subresource = subresource,
            Before = before,
            After = after
        };

        ((delegate* unmanaged[Stdcall]<nint, uint, HostTransition*, void>)Slot(list, HostResourceBarrierSlot))
            (list, 1, &barrier);
    }

    // Test uses a real guest resource, DEFAULT memory, an upload heap and a readback heap.
    // It is intentionally separate from guest execution until VUM synchronization is implemented.
    internal static void VerifyHostResourceAbi(nint device, nint queue, nint allocator)
    {
        const int roundTripBytes = 4096;
        const int patternStride = 17;
        const int patternSeed = 3;
        const int probeTextureExtent = 64;
        const uint commonState = (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;

        var data = NativeMemory.AlignedAlloc((nuint)ResourcePage, (nuint)ResourcePage);

        nint guest = 0, texture = 0, upload = 0, readback = 0, list = 0;

        var description = HostBufferDescription(roundTripBytes);
        var iid = ResourceId;
        var listId = HostListId;
        var hostDevice = (nint)_host;
        var hostQueue = Host(queue);

        try
        {
            for (var index = 0; index < roundTripBytes; index++)
            {
                ((byte*)data)[index] = (byte)(index * patternStride + patternSeed);
            }

            var create =
                (delegate* unmanaged[Stdcall]<nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(
                    device, CreateResourceAtAddressSlot);

            CheckHostResult(create(device, (ulong)data, &description, 0, 0, &iid, &guest), "Guest buffer");
            CheckHostResult(MaterializeHostResource(guest, out var local), "DEFAULT resource");
            CheckHostResult(MaterializeHostResource(guest, out var again), "Repeated materialization");

            if (local == 0 || local != again || ((ResourceObject*)guest)->Address != (ulong)data)
            {
                throw new InvalidOperationException("Host resource identity or guest address changed.");
            }

            var textureDescription = new ResourceDescription
            {
                Dimension = (uint)D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
                Width = probeTextureExtent,
                Height = probeTextureExtent,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = HostTextureViewFormats.Rgba8Unorm,
                SampleCount = 1,
                Flags = (uint)D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET
            };

            CheckHostResult(create(device, (ulong)data, &textureDescription, 0, 0, &iid, &texture), "Guest texture");

            CheckHostResult(MaterializeHostResource(texture, out var hostTexture), "DEFAULT texture allocation");

            if (hostTexture == 0)
            {
                throw new InvalidOperationException("Host texture is null.");
            }

            CheckHostResult(CreateHostResource(description, (uint)D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_UPLOAD, GenericRead,
                &upload), "UPLOAD resource");

            CheckHostResult(CreateHostResource(description, (uint)D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_READBACK,
                CopyDestination, &readback), "READBACK resource");

            var noRead = default(HostRange);

            void* mapped = null;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload, HostMapResourceSlot))
                (upload, 0, &noRead, &mapped), "Map upload");

            new ReadOnlySpan<byte>(data, roundTripBytes).CopyTo(new Span<byte>(mapped, roundTripBytes));

            var written = new HostRange { End = roundTripBytes };

            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, HostUnmapResourceSlot))
                (upload, 0, &written);

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(hostDevice,
                    HostCreateCommandListSlot))
                (hostDevice, 0, (uint)D3D12_COMMAND_LIST_TYPE.D3D12_COMMAND_LIST_TYPE_DIRECT, Host(allocator), 0,
                    &listId, &list), "Host copy list");

            var copy = (delegate* unmanaged[Stdcall]<nint, nint, ulong, nint, ulong, ulong, void>)Slot(list,
                HostCopyBufferRegionSlot);

            TransitionHostResource(list, local, commonState, CopyDestination);

            copy(list, local, 0, upload, 0, roundTripBytes);

            TransitionHostResource(list, local, CopyDestination, CopySource);

            copy(list, readback, 0, local, 0, roundTripBytes);

            TransitionHostResource(list, local, CopySource, commonState);

            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, HostCloseCommandListSlot))(list),
                "Close host copy list");

            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(hostQueue, HostExecuteCommandListsSlot))
                (hostQueue, 1, &list);

            WaitForHostCompletion(hostQueue);

            mapped = null;

            var read = new HostRange { End = roundTripBytes };

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, HostMapResourceSlot))
                (readback, 0, &read, &mapped), "Map readback");

            var same = new ReadOnlySpan<byte>(mapped, roundTripBytes).SequenceEqual(new ReadOnlySpan<byte>(data, roundTripBytes));

            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, HostUnmapResourceSlot))
                (readback, 0, &noRead);

            if (!same)
            {
                throw new InvalidOperationException("GPU resource round-trip bytes differ.");
            }

            Console.WriteLine($"[graphics-probe] DEFAULT/UPLOAD/READBACK GPU resource round trip passed ({roundTripBytes} bytes).");

            VerifyVersionedUpload(
                hostDevice,
                hostQueue,
                Host(allocator),
                local,
                upload,
                readback,
                new ReadOnlySpan<byte>(data, roundTripBytes));

            VerifyHostTextureTransfers(hostQueue);
            VerifyHostTextureTransfers(hostQueue, true);
            VerifyTextureUploadBatch(hostQueue);
            VerifyTiledBc3Transfer(hostQueue);
            if (SupportsHostTextureSampling(HostTextureViewFormats.A8Unorm))
            {
                VerifyHostTextureTransfers(hostQueue, true, HostTextureViewFormats.A8Unorm,
                    HostTextureViewFormats.A8Unorm);
            }
            else
            {
                Console.WriteLine("[graphics-probe] R77 A8 sample support unavailable; A8 transfer not tested.");
            }

            if (SupportsHostTextureSampling((uint)DXGI_FORMAT.DXGI_FORMAT_R8_UNORM))
            {
                VerifyHostTextureTransfers(hostQueue, true, (uint)DXGI_FORMAT.DXGI_FORMAT_R8_TYPELESS,
                    (uint)DXGI_FORMAT.DXGI_FORMAT_R8_UNORM);
            }
            else
            {
                Console.WriteLine(
                    "[graphics-probe] R77 R8 sample support unavailable; typeless R8 transfer not tested.");
            }

            VerifyCurveLutGpu(hostQueue);
            VerifyColorLutInputUpload(hostQueue);
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
}
