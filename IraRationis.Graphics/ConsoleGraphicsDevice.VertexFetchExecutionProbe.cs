// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Shaders;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const uint R11Width = 16;
    private const uint R11Height = 16;
    private const uint R11Pitch = 256;
    private const int R11SourceBytes = 128;
    // Opt-in proof of the translated pipeline on an independent host queue.
    // Never binds guest allocations, alters guest targets, or replaces the guest draw.
    private static readonly bool VerifyTranslatedDraw =
        Environment.GetEnvironmentVariable("IRX_VERIFY_TRANSLATED_DRAW") == "1";

    private static void VerifyR11TranslatedDraw(VertexFetchShaderPair pair, nint root, nint pipeline,
        TextWriter report)
    {
        if (!VerifyTranslatedDraw)
        {
            return;
        }

        if (pair.Position != new VertexFetchField(0, 3) || pair.Attribute != new VertexFetchField(12, 4))
        {
            report.WriteLine(
                "[graphics] IRX-GFX-DRAW-R11: synthetic fixture does not match the translated fetch layout.");

            return;
        }

        report.WriteLine("[graphics] IRX-GFX-DRAW-R11: starting isolated translated GPU draw (synthetic inputs).");

        report.Flush();

        Console.Error.WriteLine("[graphics] IRX-GFX-DRAW-R11: starting isolated translated GPU draw.");

        try
        {
            ExecuteR11TranslatedDraw(pair, root, pipeline, report);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                              OverflowException or OutOfMemoryException)
        {
            report.WriteLine($"[graphics] IRX-GFX-DRAW-R11: FAILED: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ExecuteR11TranslatedDraw(VertexFetchShaderPair pair, nint root, nint pipeline,
        TextWriter report)
    {
        nint queue = 0;
        nint allocator = 0;
        nint list = 0;
        nint upload = 0;
        nint color = 0;
        nint depth = 0;
        nint readback = 0;
        nint rtvHeap = 0;
        nint dsvHeap = 0;

        var device = (nint)_host;

        try
        {
            var queueDescription = default(QueueDescription);
            var queueId = QueueId;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, QueueDescription*, Guid*, nint*, int>)Slot(device, 8))
                (device, &queueDescription, &queueId, &queue), "R11 direct queue");

            var allocatorId = AllocatorId;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(device, 9))
                (device, 0, &allocatorId, &allocator), "R11 allocator");

            var listId = HostListId;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12))
                (device, 0, 0, allocator, pipeline, &listId, &list), "R11 command list");

            CheckHostResult(CreateHostResource(HostBufferDescription(R11SourceBytes), 2, GenericRead, &upload),
                "R11 vertex-pulling upload");

            CheckHostResult(CreateHostResource(HostBufferDescription(R11Pitch * R11Height), 3, CopyDestination,
                &readback), "R11 readback");

            var texture = new ResourceDescription
            {
                Dimension = 3,
                Width = R11Width,
                Height = R11Height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = 28,
                SampleCount = 1,
                Flags = 1
            };

            CheckHostResult(CreateHostResource(texture, 1, 0, &color), "R11 color target");

            texture.Format = 20;

            texture.Flags = 2;

            CheckHostResult(CreateHostResource(texture, 1, 0, &depth), "R11 depth target");

            if (queue == 0 || allocator == 0 || list == 0 || upload == 0 || readback == 0 || color == 0 || depth == 0)
            {
                throw new InvalidOperationException("R11 host creation returned a null object.");
            }

            WriteR11SyntheticSource(pair, upload);

            rtvHeap = CreateR11ViewHeap(device, 2);

            dsvHeap = CreateR11ViewHeap(device, 3);

            var rtv = ((ID3D12DescriptorHeap*)rtvHeap)->GetCPUDescriptorHandleForHeapStart();
            var dsv = ((ID3D12DescriptorHeap*)dsvHeap)->GetCPUDescriptorHandleForHeapStart();

            if (rtv.ptr == 0 || dsv.ptr == 0)
            {
                throw new InvalidOperationException("R11 host view heap returned an invalid handle.");
            }

            var host = (ID3D12Device*)device;

            host->CreateRenderTargetView((ID3D12Resource*)color, null, rtv);

            host->CreateDepthStencilView((ID3D12Resource*)depth, null, dsv);

            var commands = (ID3D12GraphicsCommandList*)list;
            var gpuAddress = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();

            if (gpuAddress == 0 || (gpuAddress & 3UL) != 0)
            {
                throw new InvalidOperationException("R11 upload GPU virtual address is invalid.");
            }

            TransitionHostResource(list, color, 0, 4);

            TransitionHostResource(list, depth, 0, 0x10);

            var clear = stackalloc float[4] { 0, 0, 0, 1 };

            commands->ClearRenderTargetView(rtv, clear, 0, null);

            commands->ClearDepthStencilView(dsv, (D3D12_CLEAR_FLAGS)3, 1.0f, 0, 0, null);

            commands->SetPipelineState((ID3D12PipelineState*)pipeline);

            commands->SetGraphicsRootSignature((ID3D12RootSignature*)root);

            commands->SetGraphicsRootShaderResourceView(0, gpuAddress);

            commands->IASetPrimitiveTopology((D3D_PRIMITIVE_TOPOLOGY)4);

            var viewport = new D3D12_VIEWPORT { Width = R11Width, Height = R11Height, MaxDepth = 1.0f };
            var scissor = new RECT { right = (int)R11Width, bottom = (int)R11Height };

            commands->RSSetViewports(1, &viewport);

            commands->RSSetScissorRects(1, &scissor);

            commands->OMSetRenderTargets(1, &rtv, false, &dsv);

            commands->DrawInstanced(3, 1, 0, 0);

            TransitionHostResource(list, color, 4, CopySource);

            TransitionHostResource(list, depth, 0x10, 0);

            var from = new ClearProbeCopyLocation { Resource = color };
            var to = new ClearProbeCopyLocation
            {
                Resource = readback,
                Kind = 1,
                Format = 28,
                Width = R11Width,
                Height = R11Height,
                Depth = 1,
                Pitch = R11Pitch
            };

            ((delegate* unmanaged[Stdcall]<nint, ClearProbeCopyLocation*, uint, uint, uint, ClearProbeCopyLocation*,
                void*, void>)Slot(list, 16))(list, &to, 0, 0, 0, &from, null);

            TransitionHostResource(list, color, CopySource, 0);

            CheckHostResult(commands->Close(), "R11 command list close");

            // Construct the completion fence before submission. Existing wait logic is fail-fast on
            // uncertain completion, so in-flight objects are never released on a recoverable error.
            using var completion = new HostCompletionFence();

            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &list);

            completion.Wait(queue);

            VerifyR11Readback(readback, report);
        }
        finally
        {
            // Root and PSO are borrowed from the caller and remain live through completion.
            foreach (var handle in new[] { list, rtvHeap, dsvHeap, readback, depth, color, upload, allocator, queue })
            {
                if (handle != 0)
                {
                    _ = HostRelease(handle);
                }
            }
        }
    }

    private static nint CreateR11ViewHeap(nint device, uint kind)
    {
        var description = new DescriptorHeapDescription { Kind = kind, Count = 1 };
        var iid = DescriptorHeapId;

        nint heap = 0;

        var hr = ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(device, 14))
            (device, &description, &iid, &heap);

        if (hr < 0 || heap == 0)
        {
            if (heap != 0)
            {
                _ = HostRelease(heap);
            }

            throw new InvalidOperationException($"R11 descriptor heap creation failed: 0x{hr:X8}.");
        }

        return heap;
    }

    private static void WriteR11SyntheticSource(VertexFetchShaderPair pair, nint upload)
    {
        Span<byte> source = stackalloc byte[R11SourceBytes];

        source.Clear();

        const uint positionBase = 32;
        const uint attributeBase = 80;

        BinaryPrimitives.WriteUInt32LittleEndian(source, (positionBase << 7) | (12U << 3) | 3U);

        BinaryPrimitives.WriteUInt32LittleEndian(source[12..], (attributeBase << 7) | (16U << 3) | 4U);

        ReadOnlySpan<float> positions = [-0.75f, -0.75f, 0.5f, 0.0f, 0.75f, 0.5f, 0.75f, -0.75f, 0.5f];

        for (var index = 0; index < positions.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(source[((int)positionBase + index * sizeof(float))..],
                positions[index]);
        }

        // Distinct attributes make an accidental interpolated or wrong provoking-vertex output observable.
        ReadOnlySpan<float> attributes = [0.25f, 0.5f, 0.75f, 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f];

        for (var index = 0; index < attributes.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(source[((int)attributeBase + index * sizeof(float))..],
                attributes[index]);
        }

        if (!Rdna2VertexFetchCompiler.TryGetReadExtent(pair, source, 0, 3, R11SourceBytes, out var extent) ||
            extent != R11SourceBytes)
        {
            throw new InvalidOperationException("R11 synthetic vertex source extent is inconsistent.");
        }

        var noRead = default(HostRange);

        void* mapped = null;

        CheckHostResult(
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload, 8))
            (upload, 0, &noRead, &mapped), "R11 upload map");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("R11 upload map returned null.");
            }

            source.CopyTo(new Span<byte>(mapped, R11SourceBytes));
        }
        finally
        {
            var written = new HostRange { End = mapped == null ? 0U : R11SourceBytes };

            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, 9))(upload, 0, &written);
        }
    }

    private static void VerifyR11Readback(nint readback, TextWriter report)
    {
        var range = new HostRange { End = R11Pitch * R11Height };

        void* mapped = null;

        CheckHostResult(
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8))
            (readback, 0, &range, &mapped), "R11 readback map");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("R11 readback map returned null.");
            }

            var pixels = (byte*)mapped;
            var center = pixels + R11Height / 2 * R11Pitch + R11Width / 2 * 4;
            var corner = pixels;
            var colored = 0;
            var unexpected = 0;

            for (var y = 0U; y < R11Height; y++)
            {
                for (var x = 0U; x < R11Width; x++)
                {
                    var pixel = pixels + y * R11Pitch + x * 4;

                    if (R11ExpectedColor(pixel))
                    {
                        colored++;
                    }
                    else if (pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0 || pixel[3] != 255)
                    {
                        unexpected++;
                    }
                }
            }

            report.WriteLine($"[graphics] R11 readback center=({center[0]},{center[1]},{center[2]},{center[3]}) " +
                             $"corner=({corner[0]},{corner[1]},{corner[2]},{corner[3]}) matchingPixels={colored} unexpectedPixels={unexpected}.");

            if (!R11ExpectedColor(center) || corner[0] != 0 || corner[1] != 0 || corner[2] != 0 ||
                corner[3] != 255 || colored is < 32 or > 128 || unexpected != 0)
            {
                throw new InvalidOperationException("R11 translated triangle did not match expected readback.");
            }

            report.WriteLine("[graphics] IRX-GFX-DRAW-R11: PASS: translated shaders, root SRV, native PSO, " +
                             "DrawInstanced, fence completion and GPU pixel readback (synthetic inputs only).");
        }
        finally
        {
            var written = default(HostRange);

            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9))(readback, 0, &written);
        }
    }

    private static bool R11ExpectedColor(byte* pixel)
    {
        return Math.Abs(pixel[0] - 64) <= 1 && Math.Abs(pixel[1] - 128) <= 1 &&
               Math.Abs(pixel[2] - 191) <= 1 && pixel[3] == 255;
    }
}
