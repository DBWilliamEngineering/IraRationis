// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Synchronization;
using IraRationis.Shaders.VertexFetch;
using IraRationis.Shaders.VertexFetch.Models;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const uint VertexFetchProbeWidth = 16;
    private const uint VertexFetchProbeHeight = 16;
    private const uint VertexFetchProbePitch = 256;
    private const int VertexFetchProbeSourceBytes = 144;
    private const float VertexFetchProbeAlpha = 0.625f;
    // Opt-in proof of the translated pipeline on an independent host queue.
    // Never binds guest allocations, alters guest targets, or replaces the guest draw.
    private static readonly bool VerifyTranslatedDraw =
        Environment.GetEnvironmentVariable("IRX_VERIFY_TRANSLATED_DRAW") == "1";

    internal static void VerifyDepthlessVertexFetchGpu()
    {
        var pair = Rdna2VertexFetchCompiler.VerifySyntheticRecognition();
        var report = Console.Out;
        var vertex = CompileHostShader(pair.VertexSource, "vs_5_1", report);
        var pixel = CompileHostShader(pair.PixelSource, "ps_5_1", report);
        var rootBlob = SerializeHostRoot(pair, report);

        if (vertex == null || pixel == null || rootBlob == null)
        {
            throw new InvalidOperationException("Authored A8 vertex-fetch shaders or root could not be created.");
        }

        var guest = CreateAlphaVertexFetchProbeState();
        if (!TryPrepareVertexFetchHostState(guest, false, out var hostState, out var colorFormat) ||
            colorFormat != HostTextureViewFormats.A8Unorm || ReferenceEquals(guest, hostState))
        {
            throw new InvalidOperationException("Authored A8 target and no-DSV state failed normalization.");
        }

        void* root = null;
        nint pipeline = 0;

        try
        {
            var iid = RootSignatureId;

            fixed (byte* bytes = rootBlob)
            {
                CheckHostResult(_host->CreateRootSignature(0, bytes, (nuint)rootBlob.Length, &iid, &root),
                    "authored A8 vertex-fetch root");
            }

            RequireHostObject((nint)root, "authored A8 vertex-fetch root");

            pipeline = CreatePipelineCandidate(root, vertex, pixel, hostState, report,
                allowDepthless: true, colorFormat: colorFormat);
            RequireHostObject(pipeline, "authored A8 vertex-fetch pipeline");

            ExecuteTranslatedDraw(pair, (nint)root, pipeline, colorFormat, false, report);
        }
        finally
        {
            if (pipeline != 0)
            {
                _ = HostRelease(pipeline);
            }

            if (root != null)
            {
                _ = HostRelease((nint)root);
            }
        }
    }

    private static GraphicsPipelineMetadata CreateAlphaVertexFetchProbeState()
    {
        var blend = new D3D12_BLEND_DESC();

        blend.RenderTarget[0] = new D3D12_RENDER_TARGET_BLEND_DESC
        {
            SrcBlend = D3D12_BLEND.D3D12_BLEND_ONE,
            DestBlend = D3D12_BLEND.D3D12_BLEND_ZERO,
            BlendOp = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
            SrcBlendAlpha = D3D12_BLEND.D3D12_BLEND_ONE,
            DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_ZERO,
            BlendOpAlpha = D3D12_BLEND_OP.D3D12_BLEND_OP_ADD,
            LogicOp = D3D12_LOGIC_OP.D3D12_LOGIC_OP_NOOP,
            RenderTargetWriteMask = (byte)D3D12_COLOR_WRITE_ENABLE.D3D12_COLOR_WRITE_ENABLE_ALL
        };

        var rasterizer = new D3D12_RASTERIZER_DESC
        {
            FillMode = D3D12_FILL_MODE.D3D12_FILL_MODE_SOLID,
            CullMode = D3D12_CULL_MODE.D3D12_CULL_MODE_NONE,
            DepthClipEnable = true
        };

        var targets = new D3D12_RT_FORMAT_ARRAY
        {
            NumRenderTargets = 1
        };

        targets.RTFormats[0] = DXGI_FORMAT.DXGI_FORMAT_A8_UNORM;

        var depth = new D3D12_DEPTH_STENCIL_DESC1
        {
            DepthEnable = true,
            DepthWriteMask = D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ALL,
            DepthFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_LESS
        };

        return new GraphicsPipelineMetadata(0, [],
        [
            CreateVertexFetchProbeRecord(8, blend),
            CreateVertexFetchProbeRecord(9, uint.MaxValue),
            CreateVertexFetchProbeRecord(10, rasterizer),
            CreateVertexFetchProbeRecord(14, D3D12_PRIMITIVE_TOPOLOGY_TYPE.D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE),
            CreateVertexFetchProbeRecord(15, targets),
            CreateVertexFetchProbeRecord(16, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN),
            CreateVertexFetchProbeRecord(17, new DXGI_SAMPLE_DESC { Count = 1 }),
            CreateVertexFetchProbeRecord(21, depth)
        ], true, true);
    }

    private static PipelineStateRecord CreateVertexFetchProbeRecord<T>(uint kind, T payload) where T : unmanaged
    {
        var bytes = new byte[(sizeof(uint) + sizeof(T) + 7) & ~7];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, kind);

        MemoryMarshal.Write(bytes.AsSpan(sizeof(uint)), in payload);

        return
            new PipelineStateRecord(kind, bytes);
    }

    private static void VerifyVertexFetchTranslatedDraw(VertexFetchShaderPair pair, nint root, nint pipeline,
        uint colorFormat, bool depthBound, TextWriter report)
    {
        if (!VerifyTranslatedDraw)
        {
            return;
        }

        if (pair.Position != new VertexFetchField(0, 3)
            || pair.Attribute != new VertexFetchField(12, 4))
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
            ExecuteTranslatedDraw(pair, root, pipeline, colorFormat, depthBound, report);
        }
        catch (Exception exception) when (exception
                                              is InvalidOperationException
                                              or ArgumentException
                                              or OverflowException
                                              or OutOfMemoryException)
        {
            report.WriteLine($"[graphics] IRX-GFX-DRAW-R11: FAILED: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ExecuteTranslatedDraw(VertexFetchShaderPair pair, nint root, nint pipeline,
        uint colorFormat, bool depthBound, TextWriter report)
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
            var queueDescription = default(GraphicsQueueDescription);
            var queueId = QueueId;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, GraphicsQueueDescription*, Guid*, nint*, int>)Slot(device, 8))
                (device, &queueDescription, &queueId, &queue), "R11 direct queue");

            var allocatorId = AllocatorId;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(device, 9))
                (device, 0, &allocatorId, &allocator), "R11 allocator");

            var listId = HostListId;

            CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12))
                (device, 0, 0, allocator, pipeline, &listId, &list), "R11 command list");

            CheckHostResult(
                CreateHostResource(HostBufferDescription(VertexFetchProbeSourceBytes), 2, GenericRead, &upload),
                "R11 vertex-pulling upload");

            CheckHostResult(CreateHostResource(HostBufferDescription(VertexFetchProbePitch * VertexFetchProbeHeight), 3,
                CopyDestination,
                &readback), "R11 readback");

            var texture = new ResourceDescription
            {
                Dimension = 3,
                Width = VertexFetchProbeWidth,
                Height = VertexFetchProbeHeight,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = colorFormat,
                SampleCount = 1,
                Flags = 1
            };

            CheckHostResult(CreateHostResource(texture, 1, 0, &color), "R11 color target");

            if (depthBound)
            {
                texture.Format = 20;
                texture.Flags = 2;
                CheckHostResult(CreateHostResource(texture, 1, 0, &depth), "R11 depth target");
            }

            if (queue == 0 || allocator == 0 || list == 0 || upload == 0 || readback == 0 || color == 0 ||
                (depthBound && depth == 0))
            {
                throw new InvalidOperationException("R11 host creation returned a null object.");
            }

            var alphaOnly = colorFormat == HostTextureViewFormats.A8Unorm;
            WriteSyntheticVertexFetchSource(pair, upload, alphaOnly);

            rtvHeap = CreateShaderViewHeap(device, 2);

            var rtv = ((ID3D12DescriptorHeap*)rtvHeap)->GetCPUDescriptorHandleForHeapStart();
            if (rtv.ptr == 0)
            {
                throw new InvalidOperationException("R11 host view heap returned an invalid handle.");
            }

            var host = (ID3D12Device*)device;

            host->CreateRenderTargetView((ID3D12Resource*)color, null, rtv);

            var dsv = default(D3D12_CPU_DESCRIPTOR_HANDLE);
            if (depthBound)
            {
                dsvHeap = CreateShaderViewHeap(device, 3);
                dsv = ((ID3D12DescriptorHeap*)dsvHeap)->GetCPUDescriptorHandleForHeapStart();
                if (dsv.ptr == 0)
                {
                    throw new InvalidOperationException("R11 host depth view heap returned an invalid handle.");
                }

                host->CreateDepthStencilView((ID3D12Resource*)depth, null, dsv);
            }

            var commands = (ID3D12GraphicsCommandList*)list;
            var gpuAddress = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();

            if (gpuAddress == 0 || (gpuAddress & 3UL) != 0)
            {
                throw new InvalidOperationException("R11 upload GPU virtual address is invalid.");
            }

            TransitionHostResource(list, color, 0, 4);

            var clear = stackalloc float[4] { 0, 0, 0, alphaOnly ? 0.0f : 1.0f };

            commands->ClearRenderTargetView(rtv, clear, 0, null);

            if (depthBound)
            {
                TransitionHostResource(list, depth, 0, 0x10);
                commands->ClearDepthStencilView(dsv, (D3D12_CLEAR_FLAGS)3, 1.0f, 0, 0, null);
            }

            commands->SetPipelineState((ID3D12PipelineState*)pipeline);

            commands->SetGraphicsRootSignature((ID3D12RootSignature*)root);

            commands->SetGraphicsRootShaderResourceView(0, gpuAddress);

            commands->IASetPrimitiveTopology((D3D_PRIMITIVE_TOPOLOGY)4);

            var viewport = new D3D12_VIEWPORT
                { Width = VertexFetchProbeWidth, Height = VertexFetchProbeHeight, MaxDepth = 1.0f };
            var scissor = new RECT { right = (int)VertexFetchProbeWidth, bottom = (int)VertexFetchProbeHeight };

            commands->RSSetViewports(1, &viewport);

            commands->RSSetScissorRects(1, &scissor);

            commands->OMSetRenderTargets(1, &rtv, false, depthBound ? &dsv : null);

            commands->DrawInstanced(3, 1, 0, 0);

            TransitionHostResource(list, color, 4, CopySource);

            if (depthBound)
            {
                TransitionHostResource(list, depth, 0x10, 0);
            }

            var from = new ClearProbeCopyLocation { Resource = color };
            var destination = new ClearProbeCopyLocation
            {
                Resource = readback,
                Kind = 1,
                Format = colorFormat,
                Width = VertexFetchProbeWidth,
                Height = VertexFetchProbeHeight,
                Depth = 1,
                Pitch = VertexFetchProbePitch
            };

            ((delegate* unmanaged[Stdcall]<nint, ClearProbeCopyLocation*, uint, uint, uint, ClearProbeCopyLocation*,
                void*, void>)Slot(list, 16))(list, &destination, 0, 0, 0, &from, null);

            TransitionHostResource(list, color, CopySource, 0);

            CheckHostResult(commands->Close(), "R11 command list close");

            // Construct the completion fence before submission. Existing wait logic is fail-fast on
            // uncertain completion, so in-flight objects are never released on a recoverable error.
            using var completion = new HostCompletionFence();

            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &list);

            completion.Wait(queue);

            VerifyVertexFetchProbeReadback(readback, alphaOnly, report);
        }
        finally
        {
            // Root and PSO are borrowed from the caller and remain live through completion.
            foreach (var handle in new[]
                         {
                             list,
                             rtvHeap,
                             dsvHeap,
                             readback,
                             depth,
                             color,
                             upload,
                             allocator,
                             queue
                         }
                         .Where(static handle => handle != 0))
            {
                _ = HostRelease(handle);
            }
        }
    }

    internal static nint CreateShaderViewHeap(nint device, uint kind)
    {
        var description = new DescriptorHeapDescription { Kind = kind, Count = 1 };
        var iid = DescriptorHeapId;

        nint heap = 0;

        var resultCode =
            ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(device, 14))
            (device, &description, &iid, &heap);

        if (resultCode >= 0 && heap != 0)
        {
            return heap;
        }

        if (heap != 0)
        {
            _ = HostRelease(heap);
        }

        throw new InvalidOperationException($"R11 descriptor heap creation failed: 0x{resultCode:X8}.");
    }

    private static void WriteSyntheticVertexFetchSource(VertexFetchShaderPair pair, nint upload, bool alphaOnly)
    {
        Span<byte> source = stackalloc byte[VertexFetchProbeSourceBytes];

        source.Clear();

        const uint positionBase = 48;
        const uint attributeBase = 96;

        if (pair.Position.HeaderOffset > positionBase - sizeof(uint) ||
            pair.Attribute.HeaderOffset > positionBase - sizeof(uint))
        {
            throw new InvalidOperationException("R11 authored source header would overlap vertex data.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(source[(int)pair.Position.HeaderOffset..],
            (positionBase << 7) | (12U << 3) | 3U);

        BinaryPrimitives.WriteUInt32LittleEndian(source[(int)pair.Attribute.HeaderOffset..],
            (attributeBase << 7) | (16U << 3) | 4U);

        ReadOnlySpan<float> positions = [-0.75f, -0.75f, 0.5f, 0.0f, 0.75f, 0.5f, 0.75f, -0.75f, 0.5f];

        for (var index = 0; index < positions.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(source[((int)positionBase + index * sizeof(float))..],
                positions[index]);
        }

        // Distinct attributes make an accidental interpolated or wrong provoking-vertex output observable.
        var attributes = alphaOnly
            ? new[] { 0.25f, 0.5f, 0.75f, VertexFetchProbeAlpha, 1.0f, 0.0f, 0.0f, 0.125f, 0.0f, 1.0f, 0.0f, 0.875f }
            : new[] { 0.25f, 0.5f, 0.75f, 1.0f, 1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 1.0f, 0.0f, 1.0f };

        for (var index = 0; index < attributes.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(source[((int)attributeBase + index * sizeof(float))..],
                attributes[index]);
        }

        if (!Rdna2VertexFetchCompiler.TryGetReadExtent(pair, source, 0, 3, VertexFetchProbeSourceBytes, out var extent)
            ||
            extent != VertexFetchProbeSourceBytes)
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

            source.CopyTo(new Span<byte>(mapped, VertexFetchProbeSourceBytes));
        }
        finally
        {
            var written = new HostRange { End = mapped == null ? 0U : VertexFetchProbeSourceBytes };

            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, 9))(upload, 0, &written);
        }
    }

    private static void VerifyVertexFetchProbeReadback(nint readback, bool alphaOnly, TextWriter report)
    {
        var range = new HostRange { End = VertexFetchProbePitch * VertexFetchProbeHeight };

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

            if (alphaOnly)
            {
                VerifyAlphaVertexFetchReadback(pixels, report);

                return;
            }

            var center = pixels + VertexFetchProbeHeight / 2 * VertexFetchProbePitch + VertexFetchProbeWidth / 2 * 4;
            var colored = 0;
            var unexpected = 0;

            for (var coordinateY = 0U; coordinateY < VertexFetchProbeHeight; coordinateY++)
            {
                for (var coordinateX = 0U; coordinateX < VertexFetchProbeWidth; coordinateX++)
                {
                    var pixel = pixels + coordinateY * VertexFetchProbePitch + coordinateX * 4;

                    if (VertexFetchProbeExpectedColor(pixel))
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
                             $"corner=({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]}) matchingPixels={colored} unexpectedPixels={unexpected}.");

            if (!VertexFetchProbeExpectedColor(center) || pixels[0] != 0 || pixels[1] != 0 || pixels[2] != 0 ||
                pixels[3] != 255 || colored is < 32 or > 128 || unexpected != 0)
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

    private static bool VertexFetchProbeExpectedColor(byte* pixel)
    {
        return Math.Abs(pixel[0] - 64) <= 1 && Math.Abs(pixel[1] - 128) <= 1 &&
               Math.Abs(pixel[2] - 191) <= 1 && pixel[3] == 255;
    }

    private static void VerifyAlphaVertexFetchReadback(byte* pixels, TextWriter report)
    {
        var expected = (int)Math.Round(VertexFetchProbeAlpha * byte.MaxValue);
        var center = pixels[VertexFetchProbeHeight / 2 * VertexFetchProbePitch + VertexFetchProbeWidth / 2];
        var covered = 0;
        var unexpected = 0;

        for (var row = 0U; row < VertexFetchProbeHeight; row++)
        {
            for (var column = 0U; column < VertexFetchProbeWidth; column++)
            {
                var alpha = pixels[row * VertexFetchProbePitch + column];
                if (Math.Abs(alpha - expected) <= 1)
                {
                    covered++;
                }
                else if (alpha != 0)
                {
                    unexpected++;
                }
            }
        }

        if (Math.Abs(center - expected) > 1 || pixels[0] != 0 || covered is < 32 or > 128 || unexpected != 0)
        {
            throw new InvalidOperationException(
                $"Depthless A8 vertex-fetch readback differed: center={center}, covered={covered}, unexpected={unexpected}.");
        }

        report.WriteLine(
            $"[graphics-probe] Depthless A8 vertex-fetch passed GPU alpha readback (alpha={center}, tolerance=1).");
    }
}
