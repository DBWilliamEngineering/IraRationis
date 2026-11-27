// SPDX-License-Identifier: GPL-3.0-or-later
// ABI signatures adapted from DexNom/VIctory crates/victory-gpu/src/list.rs.

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const ulong UploadBudget = 128UL << 20;
    private const ulong MaximumDeferredCopy = 1UL << 30;
    private const int MaximumRecordedTargetViews = 64;
    private static readonly ConcurrentDictionary<string, long> IncompleteOperations = new();
    private static readonly bool AllowIncompleteOperations =
        Environment.GetEnvironmentVariable("IRX_ALLOW_INCOMPLETE_RENDERING") == "1";

    private static void IncompleteOperation(nint self, string operation, string reason)
    {
        _ = StateFor(self);

        var count = IncompleteOperations.AddOrUpdate(operation, 1, static (_, previous) => previous + 1);

        if (count <= 4 || (count & (count - 1)) == 0)
        {
            Console.Error.WriteLine($"[graphics] {operation} #{count} not executed: {reason}.");
        }

        if (!AllowIncompleteOperations)
        {
            Environment.FailFast(
                $"[graphics] {operation} requires {reason}. IRX_ALLOW_INCOMPLETE_RENDERING=1 enables diagnostic-only continuation (incorrect frames possible).");
        }
    }

    private static bool TryResolveClear(nint self, ulong handle, uint kind, out nint host, out nuint cpuHandle,
        out uint viewFormat, bool reportFailure = false)
    {
        return TryResolveClear(self, handle, kind, out host, out cpuHandle, out viewFormat, out _, reportFailure);
    }

    private static bool TryResolveClear(nint self, ulong handle, uint kind, out nint host, out nuint cpuHandle,
        out uint viewFormat, out nint guestResource, bool reportFailure = false)
    {
        host = 0;

        cpuHandle = 0;

        viewFormat = 0;

        guestResource = 0;

        var state = StateFor(self);

        if (Host(self) == 0 || *(uint*)(self + 0x10) != 0 || handle == 0)
        {
            return RejectClearView(reportFailure, "invalid command list or target handle");
        }

        DescriptorToken token = default;

        nuint transferred = 0;

        if (ReadProcessMemory(-1, (void*)(nuint)handle, &token, (nuint)sizeof(DescriptorToken), &transferred) == 0 ||
            transferred != (nuint)sizeof(DescriptorToken) || token.Tag != DescriptorTag || token.Kind != kind)
        {
            return RejectClearView(reportFailure, "unreadable or wrong-kind descriptor token");
        }

        // IDs intern the descriptor bytes and resource generation. Re-read the guest slot even on a cache hit.
        var key = (kind, token.Id);

        if (state.TargetViews is { } targetViews && targetViews.TryGetValue(key, out var cached))
        {
            if (!IsResource(cached.Resource) ||
                ((ResourceObject*)cached.Resource)->Generation != cached.ResourceGeneration ||
                ((ResourceObject*)cached.Resource)->HostResource != cached.HostResource)
            {
                return RejectClearView(reportFailure, "cached target resource changed");
            }

            host = cached.HostResource;
            cpuHandle = cached.CpuHandle;
            viewFormat = cached.Format;
            guestResource = cached.Resource;

            return true;
        }

        if (state.HostViews.Count >= MaximumCommandHostViews)
        {
            return RejectClearView(reportFailure, "descriptor heap budget exhausted");
        }

        DescriptorMetadata? metadata;

        lock (DescriptorGate)
        {
            _ = DescriptorRecords.TryGetValue(token.Id, out metadata);
        }

        if (metadata == null || !IsResource(metadata.Resource))
        {
            return RejectClearView(reportFailure, "descriptor resource is missing or not live");
        }

        var resource = (ResourceObject*)metadata.Resource;
        var description = resource->Description;
        var depth = kind == 21;

        if (resource->Generation != metadata.ResourceGeneration || metadata.Kind != kind)
        {
            return RejectClearView(reportFailure, "stale or mismatched target descriptor");
        }

        if (!HostTextureViewFormats.TryResolveTargetView(description, depth, metadata.Description, out viewFormat))
        {
            if (reportFailure)
            {
                Console.Error.WriteLine($"[graphics] target view REFUSED: guestFormat={description.Format}; " +
                                        $"hostFormat={HostTextureViewFormats.BackingFormat(description)}; viewFormat={viewFormat}; "
                                        +
                                        $"dimension={description.Dimension}; slices={description.DepthOrArraySize}; mips={description.MipLevels}; "
                                        +
                                        $"samples={description.SampleCount}; flags=0x{description.Flags:X}; descriptorBytes={metadata.Description?.Length ?? 0}.");
            }

            return false;
        }

        state.RetainObject(metadata.Resource);

        var materialized = MaterializeHostResource(metadata.Resource, out host);

        if (materialized < 0 || host == 0)
        {
            return RejectClearView(reportFailure, $"host resource allocation HRESULT=0x{materialized:X8}");
        }

        var heapDescription = new DescriptorHeapDescription { Kind = depth ? 3U : 2U, Count = 1 };
        var iid = DescriptorHeapId;
        var device = (nint)_host;

        _ = state.HostViews.EnsureCapacity(state.HostViews.Count + 1);

        nint heap = 0;

        var resultCode =
            ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(device, 14))
            (device, &heapDescription, &iid, &heap);

        if (resultCode < 0 || heap == 0)
        {
            if (heap != 0)
            {
                _ = HostRelease(heap);
            }

            return RejectClearView(reportFailure, $"host view heap HRESULT=0x{resultCode:X8}");
        }

        // TerraFX handles the Windows COM structure-return ABI.
        cpuHandle = ((ID3D12DescriptorHeap*)heap)->GetCPUDescriptorHandleForHeapStart().ptr;

        if (cpuHandle == 0)
        {
            _ = HostRelease(heap);

            return RejectClearView(reportFailure, "host CPU descriptor handle is null");
        }

        state.HostViews.Add(heap);

        // Host descriptors are Windows structures, not console descriptor bytes.
        var descriptorHandle = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = cpuHandle };

        if (depth)
        {
            var viewDescription = new D3D12_DEPTH_STENCIL_VIEW_DESC
            {
                Format = (DXGI_FORMAT)viewFormat,
                ViewDimension = D3D12_DSV_DIMENSION.D3D12_DSV_DIMENSION_TEXTURE2D
            };

            ((ID3D12Device*)device)->CreateDepthStencilView((ID3D12Resource*)host,
                &viewDescription, descriptorHandle);
        }
        else
        {
            var viewDescription = new D3D12_RENDER_TARGET_VIEW_DESC
            {
                Format = (DXGI_FORMAT)viewFormat,
                ViewDimension = D3D12_RTV_DIMENSION.D3D12_RTV_DIMENSION_TEXTURE2D
            };

            ((ID3D12Device*)device)->CreateRenderTargetView((ID3D12Resource*)host,
                &viewDescription, descriptorHandle);
        }

        state.TargetViews ??= new Dictionary<(uint Kind, ulong DescriptorId), RecordedTargetView>();

        if (state.TargetViews.Count < MaximumRecordedTargetViews)
        {
            state.TargetViews.Add(key, new RecordedTargetView(metadata.Resource, metadata.ResourceGeneration, host,
                cpuHandle, viewFormat));
        }

        guestResource = metadata.Resource;

        return true;
    }

    private static bool RejectClearView(bool reportFailure, string reason)
    {
        if (reportFailure)
        {
            Console.Error.WriteLine($"[graphics] target view REFUSED: {reason}.");
        }

        return false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ClearTarget(nint self, ulong handle, float* color, uint count, RECT* rects)
    {
        if (color == null || count > 4096 || (count != 0 && rects == null)
            || !TryResolveClear(self, handle, 20, out var resource, out var view, out _))
        {
            IncompleteOperation(self, "ClearRenderTargetView", "a supported host RTV and clear description");

            return;
        }

        var list = Host(self);

        TransitionHostResource(list, resource, 0, 4);

        RecordHostGpuCommand(self, "ClearRenderTargetView");
        // Guest slot 52 is Windows BeginQuery, not ClearRenderTargetView.
        ((ID3D12GraphicsCommandList*)list)->ClearRenderTargetView(
            new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = view }, color, count, rects);

        TransitionHostResource(list, resource, 4, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ClearDepth(nint self, ulong handle, uint flags, float depth, byte stencil, uint count,
        RECT* rects)
    {
        if (flags is 0 or > 3 || !float.IsFinite(depth) || depth < 0 || depth > 1 || count > 4096 ||
            (count != 0 && rects == null)
            || !TryResolveClear(self, handle, 21, out var resource, out var view, out var viewFormat))
        {
            IncompleteOperation(self, "ClearDepthStencilView", "a supported host DSV and clear description");

            return;
        }

        if ((flags & 2) != 0 && viewFormat is not (20 or 45))
        {
            IncompleteOperation(self, "ClearDepthStencilView", "a stencil-capable format");

            return;
        }

        var list = Host(self);

        TransitionHostResource(list, resource, 0, 0x10);

        RecordHostGpuCommand(self, "ClearDepthStencilView");
        // Guest slot 51 is Windows DiscardResource, not ClearDepthStencilView.
        ((ID3D12GraphicsCommandList*)list)->ClearDepthStencilView(
            new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = view }, (D3D12_CLEAR_FLAGS)flags, depth, stencil, count, rects);

        TransitionHostResource(list, resource, 0x10, 0);
    }

    private static bool BufferAddress(nint value, ulong offset, ulong bytes, out ulong address)
    {
        address = 0;

        if (!IsResource(value))
        {
            return false;
        }

        var resource = (ResourceObject*)value;

        if (resource->Description.Dimension != 1 || resource->HostResource != 0 || offset > resource->Description.Width
            ||
            bytes > resource->Description.Width - offset || resource->Address == 0
            || offset > ulong.MaxValue - resource->Address)
        {
            return false;
        }

        address = resource->Address + offset;

        return bytes <= ulong.MaxValue - address;
    }

    private static void RecordDeferredCopy(nint self, ulong destination, ulong source, ulong bytes)
    {
        var state = StateFor(self);

        if (bytes == 0 || destination == source)
        {
            return;
        }

        if (bytes > MaximumDeferredCopy || destination == 0 || source == 0 || bytes > ulong.MaxValue - destination ||
            bytes > ulong.MaxValue - source || state.MemoryBoundaries.Count >= MaximumMemoryOperations)
        {
            Environment.FailFast("[graphics] Invalid deferred copy range.");
        }

        SealMemoryBoundary(self, state, null, null, new DeferredCopy(destination, source, bytes));
    }

    private static void ExecuteDeferredCopy(DeferredCopy copy)
    {
        if (copy.Texture is { } texture)
        {
            ExecuteLinearTextureTransfer(texture);

            return;
        }

        const int chunkSize = 65536;

        var scratch = ArrayPool<byte>.Shared.Rent(chunkSize);

        try
        {
            var backwards = copy.Destination > copy.Source && copy.Destination - copy.Source < copy.Size;

            ulong done = 0;

            fixed (byte* buffer = scratch)
            {
                while (done < copy.Size)
                {
                    var count = (nuint)Math.Min(chunkSize, copy.Size - done);
                    var offset = backwards ? copy.Size - done - count : done;

                    nuint transferred = 0;

                    if (ReadProcessMemory(-1, (void*)(nuint)(copy.Source + offset), buffer, count, &transferred) == 0
                        || transferred != count ||
                        WriteProcessMemory(-1, (void*)(nuint)(copy.Destination + offset), buffer, count, &transferred)
                        == 0 || transferred != count)
                    {
                        Environment.FailFast("[graphics] Deferred guest memory copy encountered an unmapped range.");
                    }

                    done += count;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void CopyBufferCore(nint self, nint dst, ulong dstOffset, nint src, ulong srcOffset, ulong size)
    {
        if (size == 0)
        {
            return;
        }

        if (!BufferAddress(dst, dstOffset, size, out var to) || !BufferAddress(src, srcOffset, size, out var from))
        {
            IncompleteOperation(self, "CopyBufferRegion", "CPU-backed buffer ranges or GPU coherence");

            return;
        }

        var state = StateFor(self);

        state.RetainObject(dst);
        state.RetainObject(src);

        RecordDeferredCopy(self, to, from, size);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyBuffer(nint self, nint dst, ulong dstOffset, nint src, ulong srcOffset, ulong size)
    {
        CopyBufferCore(self, dst, dstOffset, src, srcOffset, size);
    }

    private static void CopyWholeCore(nint self, nint dst, nint src)
    {
        if (!IsResource(dst) || !IsResource(src) || ((ResourceObject*)dst)->Description.Dimension != 1 ||
            ((ResourceObject*)src)->Description.Dimension != 1 || ((ResourceObject*)dst)->Description.Width
            != ((ResourceObject*)src)->Description.Width)
        {
            IncompleteOperation(self, "CopyResource", "texture transfer or matching buffer dimensions");

            return;
        }

        CopyBufferCore(self, dst, 0, src, 0, ((ResourceObject*)src)->Description.Width);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyWhole(nint self, nint dst, nint src)
    {
        if (LinearTextureTransferEnabled && TryRecordWholeLinearTexture(self, dst, src))
        {
            return;
        }

        CopyWholeCore(self, dst, src);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyWholeX(nint self, nint dst, nint src, uint flags)
    {
        CopyWholeCore(self, dst, src);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyRaw(nint self, ulong dst, ulong src, ulong size, uint flags)
    {
        // Console raw CPU addresses; cannot yet resolve GPU-written pages.
        RecordDeferredCopy(self, dst, src, size);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint AllocateListUpload(nint self, ulong size, ulong alignment)
    {
        var state = StateFor(self);

        alignment = Math.Max(alignment, 16UL);

        if (size == 0 || alignment > 2UL << 20 || (alignment & (alignment - 1)) != 0 ||
            size > UploadBudget || size > ulong.MaxValue - alignment)
        {
            return 0;
        }

        var rounded = (size + alignment - 1) & ~(alignment - 1);

        if (rounded > UploadBudget - state.UploadBytes)
        {
            return 0;
        }

        void* memory = null;

        try
        {
            memory = NativeMemory.AlignedAlloc((nuint)rounded, (nuint)alignment);

            if (memory == null)
            {
                return 0;
            }

            NativeMemory.Clear(memory, (nuint)rounded);

            state.Uploads.Add((nint)memory);

            state.UploadBytes += rounded;

            return (nint)memory;
        }
        catch (OutOfMemoryException)
        {
            NativeMemory.AlignedFree(memory);

            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordDepthBounds(nint self, float min, float max)
    {
        StateFor(self).DepthBounds = (min, max);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void IgnoreListHint(nint self)
    {
        _ = StateFor(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong DriverHint(nint self, ulong feature, ulong value)
    {
        _ = StateFor(self);

        return 0;
    }

    // All remaining known signatures are typed, but deliberately do not issue invalid host work.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DrawGuest(nint self, uint vertices, uint instances, uint first, uint firstInstance)
    {
        var recorded = StateFor(self).Topology switch
        {
            TriangleStripTopology => TryRecordPlanarColorGuestDraw(self, vertices, instances, first, firstInstance),
            RectangleRectangleTopology => TryRecordTexturedGuestDraw(self, vertices, instances, first, firstInstance) ||
                                          TryRecordRectangleGuestDraw(self, vertices, instances, first, firstInstance),
            _ => TryRecordVertexFetchGuestDraw(self, vertices, instances, first, firstInstance)
        };

        if (recorded)
        {
            return;
        }

        CaptureBlockedDraw(self, false, vertices, instances, first, 0, firstInstance);

        IncompleteOperation(self, "DrawInstanced", "translated graphics pipeline and native descriptor bindings");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DrawIndexedGuest(nint self, uint indices, uint instances, uint first, int baseVertex,
        uint firstInstance)
    {
        if (TryRecordIndexedUi(self, indices, instances, first, baseVertex, firstInstance))
        {
            return;
        }

        // Both rectangle and triangle-list paths validate their shader and IA contracts.
        if (StateFor(self).Topology is RectangleRectangleTopology or 4 &&
            TryRecordRectangleGuestDraw(self, indices, instances, first, firstInstance, true, baseVertex))
        {
            return;
        }

        CaptureBlockedDraw(self, true, indices, instances, first, baseVertex, firstInstance);

        IncompleteOperation(self, "DrawIndexedInstanced", "translated graphics pipeline and index buffer binding");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DispatchGuest(nint self, uint coordinateX, uint coordinateY, uint coordinateZ)
    {
        if (TryRecordColorLutCompute(self, coordinateX, coordinateY, coordinateZ))
        {
            return;
        }

        CaptureCompute(self, coordinateX, coordinateY, coordinateZ);

        IncompleteOperation(self, "Dispatch", "translated compute pipeline and native descriptor bindings");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyTextureGuest(nint self, nint dst, uint coordinateX, uint coordinateY, uint coordinateZ,
        nint src, nint box)
    {
        if (LinearTextureTransferEnabled
            && TryRecordLinearTextureTransfer(self, dst, coordinateX, coordinateY, coordinateZ, src, box))
        {
            return;
        }

        IncompleteOperation(self, "CopyTextureRegion", "unsupported texture layout, copy ABI or resource coherence");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ResolveGuest(nint self, nint dst, uint dstSub, nint src, uint srcSub, uint format)
    {
        IncompleteOperation(self, "ResolveSubresource", "multisample materialization and texture coherence");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void BeginGuestQuery(nint self, nint heap, uint kind, uint index)
    {
        IncompleteOperation(self, "BeginQuery", "native query heap recording");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void EndGuestQuery(nint self, nint heap, uint kind, uint index)
    {
        IncompleteOperation(self, "EndQuery", "native query heap recording");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void ResolveGuestQuery(nint self, nint heap, uint kind, uint first, uint count, nint buffer,
        ulong offset)
    {
        IncompleteOperation(self, "ResolveQueryData", "native query results and GPU-to-CPU coherence");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void IndirectGuest(nint self, nint signature, uint max, nint arguments, ulong offset, nint count,
        ulong countOffset)
    {
        IncompleteOperation(self, "ExecuteIndirect", "translated command signatures and pipeline execution");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void DispatchGuestX(nint self, uint coordinateX, uint coordinateY, uint coordinateZ, uint flags)
    {
        if (TryRecordColorLutCompute(self, coordinateX, coordinateY, coordinateZ, flags))
        {
            return;
        }

        CaptureCompute(self, coordinateX, coordinateY, coordinateZ, flags);

        IncompleteOperation(self, "DispatchX", "translated compute pipeline and console dispatch flags");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void CopyTextureGuestX(nint self, nint dst, uint coordinateX, uint coordinateY, uint coordinateZ,
        nint src, nint box, uint flags)
    {
        IncompleteOperation(self, "CopyTextureRegionX", "texture upload/readback and resource coherence");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void MeshGuestX(nint self, uint coordinateX, ulong flags, ulong indirect, ulong payload)
    {
        IncompleteOperation(self, "DispatchMeshX", "translated mesh pipeline and indirect payload");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void MeshGuest(nint self, uint coordinateX, uint coordinateY, uint coordinateZ)
    {
        IncompleteOperation(self, "DispatchMesh", "translated mesh pipeline");
    }

    private static void RegisterCommandOperations(nint* table, bool newer)
    {
        var shift = newer ? 2 : 0;

        table[12] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)&DrawGuest;

        table[13] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, void>)&DrawIndexedGuest;

        table[14] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)&DispatchGuest;

        table[15] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong, nint, ulong, ulong, void>)&CopyBuffer;

        table[16] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint, nint, void>)&CopyTextureGuest;

        table[17] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, void>)&CopyWhole;

        table[19] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, uint, uint, void>)&ResolveGuest;

        table[28] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void>)&BeginGuestQuery;

        table[29] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, void>)&EndGuestQuery;

        table[30] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint, ulong, void>)&ResolveGuestQuery;

        table[51] =
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, float, byte, uint, RECT*, void>)&ClearDepth;

        table[52] =
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, float*, uint, RECT*, void>)&ClearTarget;

        table[59 + shift] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, ulong, nint, ulong, void>)&IndirectGuest;

        table[60 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, ulong, uint, void>)&CopyRaw;

        table[68 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, float, float, void>)&RecordDepthBounds;

        table[73 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&IgnoreListHint;

        table[78 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, void>)&DispatchGuestX;

        table[83 + shift] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint, nint, uint, void>)
            &CopyTextureGuestX;

        table[84 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)&CopyWholeX;

        table[98 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, ulong, ulong, void>)&MeshGuestX;

        table[99 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, ulong>)&DriverHint;

        table[124 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&IgnoreListHint;

        table[127 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)&MeshGuest;

        if (!newer)
        {
            return;
        }

        table[142] = (nint)(delegate* unmanaged[Stdcall]<nint, void>)&IgnoreListHint;

        table[148] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, nint>)&AllocateListUpload;
    }

    private static void ValidateKnownListRegistration()
    {
        foreach (var slot in ListMethods.Keys)
        {
            var newer = slot >= 59 ? slot + 2 : slot;

            if (((nint*)_listOld)[slot] == MissingAddress(slot) || ((nint*)_listNew)[newer] == MissingAddress(newer))
            {
                throw new InvalidOperationException($"Known list slot 0x{slot * 8:X} is not registered.");
            }
        }

        foreach (var slot in new[] { 142, 146, 148 })
        {
            if (((nint*)_listNew)[slot] == MissingAddress(slot))
            {
                throw new InvalidOperationException($"Known new list slot 0x{slot * 8:X} is not registered.");
            }
        }
    }

    private static string? CommandOperationsStatus(string kind, int slot)
    {
        if (kind is not ("list-old" or "list-new"))
        {
            return null;
        }

        if (kind != "list-new")
        {
            return slot switch
            {
                51 or 52 => "registered-limited-host-clear",
                15 or 17 or 60 or 84 => "registered-cpu-deferred-copy",
                68 => "registered-cpu-state-only",
                73 or 99 or 124 => "registered-optional-noop",
                12 => "registered-experimental-host-draw",
                13 or 14 or 16 or 19 or 28 or 29 or 30 or 59 or 78 or 83 or 98 or 127 =>
                    "registered-execution-blocked",
                _ => null
            };
        }

        switch (slot)
        {
            case 142:
                return "registered-optional-noop";

            case 148:
                return "registered-cpu-only";

            case >= 61:
                slot -= 2;

                break;

            case >= 59:
                return null;
        }

        return slot switch
        {
            51 or 52 => "registered-limited-host-clear",
            15 or 17 or 60 or 84 => "registered-cpu-deferred-copy",
            68 => "registered-cpu-state-only",
            73 or 99 or 124 => "registered-optional-noop",
            12 => "registered-experimental-host-draw",
            13 or 14 or 16 or 19 or 28 or 29 or 30 or 59 or 78 or 83 or 98 or 127 =>
                "registered-execution-blocked",
            _ => null
        };
    }

    internal static nint RecordHostClearProbe(nint device, nint list,
        uint resourceFormat = 28, uint viewFormat = 0, float red = 1.0f)
    {
        var state = StateFor(list);
        var backing = NativeMemory.AlignedAlloc((nuint)ResourcePage, (nuint)ResourcePage);

        if (backing == null)
        {
            throw new OutOfMemoryException();
        }

        state.Uploads.Add((nint)backing);

        var description = new ResourceDescription
        {
            Dimension = 3,
            Width = 4,
            Height = 4,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = resourceFormat,
            SampleCount = 1,
            Flags = resourceFormat == 29 ? 0xC00001U : 1U
        };
        var iid = ResourceId;

        nint guest = 0;

        var create =
            (delegate* unmanaged[Stdcall]<nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(device,
                133);

        CheckHostResult(create(device, (ulong)backing, &description, 0, 0, &iid, &guest), "Clear probe guest texture");

        try
        {
            // Production OM binding may allocate the target before draw validation.
            CheckHostResult(MaterializeHostResource(guest, out var initialHost), "Clear probe pre-materialization");

            var descriptor = stackalloc byte[128];
            var rtv = (delegate* unmanaged[Stdcall]<nint, nint, uint*, ulong, void>)Slot(device, 20);

            var view = stackalloc uint[6] { viewFormat, 4, 0, 0, 0, 0 };

            rtv(device, guest, viewFormat == 0 ? null : view, (ulong)descriptor);

            if (!TryResolveClear(list, (ulong)descriptor, 20, out var firstHost, out var firstHandle, out _) ||
                firstHost != initialHost)
            {
                throw new InvalidOperationException("Clear probe failed to resolve a target descriptor.");
            }

            var viewsBeforeClear = state.HostViews.Count;

            if (resourceFormat == 29 && viewFormat == 28)
            {
                view[0] = 29;

                rtv(device, guest, view, (ulong)descriptor);

                if (!TryResolveClear(list, (ulong)descriptor, 20, out var rewrittenHost, out var rewrittenHandle,
                        out var rewrittenFormat) || rewrittenHost != firstHost || rewrittenHandle == firstHandle ||
                    rewrittenFormat != 29 || state.HostViews.Count != viewsBeforeClear + 1)
                {
                    throw new InvalidOperationException(
                        "Clear probe reused a stale descriptor after a guest slot rewrite.");
                }

                view[0] = viewFormat;

                rtv(device, guest, view, (ulong)descriptor);

                viewsBeforeClear = state.HostViews.Count;
            }

            var color = stackalloc float[4] { red, 0, 1, 1 };
            var clear = (delegate* unmanaged[Stdcall]<nint, ulong, float*, uint, RECT*, void>)Slot(list, 52);

            clear(list, (ulong)descriptor, color, 0, null);

            var host = ((ResourceObject*)guest)->HostResource;

            if (host == 0 || host != initialHost || state.HostViews.Count != viewsBeforeClear ||
                ((ResourceObject*)guest)->Description.Format != resourceFormat)
            {
                throw new InvalidOperationException("Clear probe did not reuse the original host RTV.");
            }

            nint readback = 0;

            CheckHostResult(CreateHostResource(HostBufferDescription(1024), 3, CopyDestination, &readback),
                "Clear probe readback");

            state.HostViews.Add(readback);

            var from = new ClearProbeCopyLocation { Resource = host };
            var destination = new ClearProbeCopyLocation
            {
                Resource = readback,
                Kind = 1,
                // Raw RGBA8 bits; the copy does not perform color conversion.
                Format = 28,
                Width = 4,
                Height = 4,
                Depth = 1,
                Pitch = 256
            };
            var command = Host(list);

            TransitionHostResource(command, host, 0, CopySource);

            RecordHostGpuCommand(list, "ProbeCopyTextureRegion");

            ((delegate* unmanaged[Stdcall]<nint, ClearProbeCopyLocation*, uint, uint, uint, ClearProbeCopyLocation*,
                    void*, void>)Slot(command, 16))
                (command, &destination, 0, 0, 0, &from, null);

            TransitionHostResource(command, host, CopySource, 0);

            return readback;
        }
        finally
        {
            _ = Drop(guest);
        }
    }

    internal static void VerifyHostClearProbe(nint readback, byte expectedRed = 255, int tolerance = 0)
    {
        var range = new HostRange { End = 1024 };

        void* mapped = null;

        CheckHostResult(
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8))(readback, 0, &range,
                &mapped), "Map clear probe readback");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("GPU clear readback mapping returned null.");
            }

            // Verify every pixel in the small authored target.
            for (var coordinateY = 0; coordinateY < 4; coordinateY++)
            {
                for (var coordinateX = 0; coordinateX < 4; coordinateX++)
                {
                    var pixel = (byte*)mapped + coordinateY * 256 + coordinateX * 4;

                    if (Math.Abs(pixel[0] - expectedRed) > tolerance || pixel[1] != 0 ||
                        pixel[2] != 255 || pixel[3] != 255)
                    {
                        throw new InvalidOperationException(
                            $"GPU clear readback differs: expected R={expectedRed} +/- {tolerance}, G=0, B=255, A=255; "
                            +
                            $"actual={pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}.");
                    }
                }
            }
        }
        finally
        {
            var written = default(HostRange);

            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9))(readback, 0, &written);
        }

        Console.WriteLine(
            $"[graphics-probe] Native RTV clear and readback passed (16 RGBA8 pixels; R={expectedRed} +/- {tolerance}).");
    }

    private static void VerifyKnownListOperations()
    {
        ValidateKnownListRegistration();

        VerifyDrawEvidence();

        var list = (nint)NativeMemory.AllocZeroed(0x980);

        try
        {
            ((GraphicsObjectHeader*)list)->Table = _listNew;

            LiveObjects[list] = _listNew;

            var allocate = (delegate* unmanaged[Stdcall]<nint, ulong, ulong, nint>)Slot(list, 148);
            var memory = allocate(list, 128, 256);

            if (memory == 0 || (nuint)memory % 256 != 0 || allocate(list, 64, 48) != 0)
            {
                throw new InvalidOperationException("List-owned upload allocation ABI failed.");
            }

            var data = new Span<byte>((void*)memory, 128);

            for (var index = 0; index < data.Length; index++)
            {
                data[index] = (byte)index;
            }

            var copy = (delegate* unmanaged[Stdcall]<nint, ulong, ulong, ulong, uint, void>)Slot(list, 62);

            copy(list, (ulong)memory + 8, (ulong)memory, 64, 0);

            if (data[8] != 8 || StateFor(list).MemoryBoundaries.Count != 1
                             || StateFor(list).MemoryBoundaries[0].Copy is not { } deferred)
            {
                throw new InvalidOperationException("Deferred copy executed during recording or was not registered.");
            }

            ExecuteDeferredCopy(deferred);

            for (var index = 0; index < 64; index++)
            {
                if (data[index + 8] != index)
                {
                    throw new InvalidOperationException("Overlapping deferred copy failed.");
                }
            }
        }
        finally
        {
            ReleaseCommandState(list);

            _ = LiveObjects.TryRemove(list, out _);

            NativeMemory.Free((void*)list);
        }

        Console.WriteLine(
            "[graphics-probe] Known command-list registration, aligned upload ownership and deferred overlapping copy passed (CPU only).");
    }
}
