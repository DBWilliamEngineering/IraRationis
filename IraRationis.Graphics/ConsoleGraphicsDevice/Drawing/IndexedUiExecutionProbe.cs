// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const uint IndexedUiProbeComponentMapping = 5768;
    private const float IndexedUiProbeCoverageThreshold = 0.5f;
    private const float IndexedUiProbeCoverageAboveThreshold = 0.75f;
    private const float IndexedUiProbeCoverageBelowThreshold = 0.25f;

    internal static void VerifyIndexedUiGpu(nint queue)
    {
        VerifyIndexedUiGpu(queue, IndexedUiVariant.BatchedMask);
        VerifyIndexedUiGpu(queue, IndexedUiVariant.Dithered);
        VerifyIndexedUiGpu(queue, IndexedUiVariant.DualAlphaMask);
        VerifyIndexedUiGpu(queue, IndexedUiVariant.BatchedDualTexture);
    }

    private static void VerifyIndexedUiGpu(nint queue, IndexedUiVariant variant)
    {
        var alphaOnly = variant == IndexedUiVariant.Dithered;

        var indexedUiProbeProgram = IndexedUiProbeProgram(variant);

        var vertex = CompileHostShader(indexedUiProbeProgram.Vertex, IndexedUiVertexShaderProfile, Console.Out);
        var pixel = CompileHostShader(indexedUiProbeProgram.Pixel, IndexedUiPixelShaderProfile, Console.Out);
        var rootBytes = SerializeIndexedUiRoot(IndexedUiCompiler.TextureCount(variant) == 2,
            IndexedUiUsesSampler(variant));

        if (vertex == null || pixel == null || rootBytes == null)
        {
            throw new InvalidOperationException($"{indexedUiProbeProgram.Name} shader or root compilation failed.");
        }

        var template = CreateAlphaVertexFetchProbeState();
        var formats = new D3D12_RT_FORMAT_ARRAY { NumRenderTargets = 1 };

        formats.RTFormats[0] =
            alphaOnly ? DXGI_FORMAT.DXGI_FORMAT_A8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;

        var depthFormat =
            alphaOnly ? DXGI_FORMAT.DXGI_FORMAT_UNKNOWN : DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT;

        var blend = template.State.Single(static record => record.Kind == 8);

        if (alphaOnly)
        {
            var alphaBlend = MemoryMarshal.Read<D3D12_BLEND_DESC>(blend.Bytes.AsSpan(4));

            alphaBlend.RenderTarget[0].BlendEnable = true;
            alphaBlend.RenderTarget[0].SrcBlend = D3D12_BLEND.D3D12_BLEND_SRC_ALPHA;
            alphaBlend.RenderTarget[0].DestBlend = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;
            alphaBlend.RenderTarget[0].DestBlendAlpha = D3D12_BLEND.D3D12_BLEND_INV_SRC_ALPHA;

            blend = CreateVertexFetchProbeRecord(8, alphaBlend);
        }

        var state = new GraphicsPipelineMetadata(0, [],
        [
            .. template.State.Where(static record => record.Kind is not (8 or 15 or 16 or 21)),
            blend,
            CreateVertexFetchProbeRecord(15, formats),
            CreateVertexFetchProbeRecord(16, depthFormat),
            CreateVertexFetchProbeRecord(21, new D3D12_DEPTH_STENCIL_DESC1
            {
                DepthFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_ALWAYS,
                FrontFace = RasterStencil(),
                BackFace = RasterStencil()
            })
        ], true, true, InputLayout: indexedUiProbeProgram.Elements);

        if (!TryPrepareIndexedUiHostState(state, variant, out var hostState, out var colorFormat) ||
            colorFormat != (uint)formats.RTFormats[0])
        {
            throw new InvalidOperationException($"{indexedUiProbeProgram.Name} target formats or depth state rejected.");
        }

        if (alphaOnly && TryPrepareIndexedUiHostState(state, IndexedUiVariant.BatchedMask, out _, out _))
        {
            throw new InvalidOperationException("Indexed UI A8 support accepted an unverified shader variant.");
        }

        void* root = null;
        nint pipeline = 0;

        try
        {
            var iid = RootSignatureId;
            fixed (byte* bytes = rootBytes)
            {
                CheckHostResult(_host->CreateRootSignature(0, bytes, (nuint)rootBytes.Length, &iid, &root),
                    $"{indexedUiProbeProgram.Name} root");
            }

            RequireHostObject((nint)root, $"{indexedUiProbeProgram.Name} root");

            pipeline =
                CreatePlanarColorPipeline(root, vertex, pixel, hostState, indexedUiProbeProgram.Elements, Console.Out, colorFormat);

            RequireHostObject(pipeline, $"{indexedUiProbeProgram.Name} pipeline");
            RunIndexedUiGpuDraw(Host(queue), (nint)root, pipeline, variant, colorFormat);

            if (variant == IndexedUiVariant.BatchedDualTexture)
            {
                RunIndexedUiGpuDraw(Host(queue), (nint)root, pipeline, variant, colorFormat, depthCoverage: true);
            }
        }
        finally
        {
            ReleaseHostObject(pipeline);
            ReleaseHostObject((nint)root);
        }
    }

    private static (string Name, string Vertex, string Pixel, VertexInputElement[] Elements) IndexedUiProbeProgram(
        IndexedUiVariant variant)
    {
        return variant switch
        {
            IndexedUiVariant.Dithered => ("dithered indexed UI A8", IndexedUiCompiler.VertexSource,
                IndexedUiCompiler.PixelSource,
                [
                    new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0),
                    new VertexInputElement("POSITION", 0, 16, 0, 4, 0, 0)
                ]),
            IndexedUiVariant.DualAlphaMask => ("dual-alpha indexed UI", IndexedUiCompiler.DualAlphaVertexSource,
                IndexedUiCompiler.DualAlphaPixelSource, [
                    new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0),
                    new VertexInputElement("TEXCOORD", 0, 16, 0, 4, 0, 0),
                    new VertexInputElement("POSITION", 0, 16, 0, 12, 0, 0)
                ]),
            IndexedUiVariant.BatchedMask or IndexedUiVariant.BatchedDualTexture => (
                variant == IndexedUiVariant.BatchedMask ? "batched indexed UI" : "batched dual-texture indexed UI",
                variant == IndexedUiVariant.BatchedMask ? IndexedUiCompiler.BatchedMaskVertexSource :
                    IndexedUiCompiler.BatchedDualTextureVertexSource,
                variant == IndexedUiVariant.BatchedMask ? IndexedUiCompiler.BatchedMaskPixelSource :
                    IndexedUiCompiler.BatchedDualTexturePixelSource, [
                    new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0),
                    new VertexInputElement("COLOR", 1, 28, 0, 4, 0, 0),
                    new VertexInputElement("POSITION", 0, 16, 0, 8, 0, 0)
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
    }

    private static void RunIndexedUiGpuDraw(nint queue, nint root, nint pipeline, IndexedUiVariant variant,
        uint colorFormat, bool depthCoverage = false)
    {
        const int width = 8;
        const int height = 4;
        const int rowPitch = 256;
        const int vertexOffset = 512;
        const int textureOffset = 1024;
        const int secondaryTextureOffset = 1536;
        const int pixelConstantOffset = IndexedUiConstantAlignment;
        const int dualTextureTexels = 4;

        var alphaOnly = colorFormat == HostTextureViewFormats.A8Unorm;
        var dualMask = variant == IndexedUiVariant.DualAlphaMask;
        var dualTexture = variant == IndexedUiVariant.BatchedDualTexture;
        var usesSampler = IndexedUiUsesSampler(variant);
        var textureWidth = dualTexture ? dualTextureTexels : 2;
        var textureFormat = dualMask ? HostTextureViewFormats.A8Unorm : (uint)DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        var coverageTextureFormat = depthCoverage
            ? HostTextureViewFormats.DepthPlaneViewFormat
            : (uint)DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT;
        var secondaryTextureFormat = dualMask ? HostTextureViewFormats.A8Unorm : coverageTextureFormat;
        var secondaryView = CreateIndexedUiProbeTextureView(secondaryTextureFormat);
        var stride = (int)IndexedUiCompiler.VertexStride(variant);
        var indexOffset = vertexOffset + 8 * stride;

        ReadOnlySpan<ushort> indices = [0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7];

        var upload = new byte[alphaOnly ? secondaryTextureOffset + rowPitch * height :
            dualMask || dualTexture ? secondaryTextureOffset + rowPitch : textureOffset + rowPitch];

        var constants = MemoryMarshal.Cast<byte, float>(upload.AsSpan(0, vertexOffset));

        if (alphaOnly)
        {
            constants.Slice(4, 4).Fill(1);

            constants[8] = 1;
            constants[13] = 1;
            constants[16] = 0.5f;
            constants[19] = 0.5f;
            constants[23] = 0.5f;

            MemoryMarshal.Cast<byte, float>(upload.AsSpan(secondaryTextureOffset)).Fill(0.5f);
        }
        else if (dualMask)
        {
            constants.Slice(4, 4).Fill(1);

            constants[8] = 0.5f;
            constants[11] = 0.5f;
            constants[15] = 0.5f;
            constants[16] = 1;
            constants[21] = 1;
        }
        else if (dualTexture)
        {
            var batchFloats = IndexedUiCompiler.BatchConstantBytes(variant) / sizeof(float);
            WriteIndexedUiDualTextureProbeBatch(constants.Slice(batchFloats, batchFloats), -0.5f, true);
            WriteIndexedUiDualTextureProbeBatch(constants.Slice(2 * batchFloats, batchFloats), 0.5f, false);
            WriteIndexedUiDualTextureProbeInputs(upload, pixelConstantOffset, textureOffset, width, height,
                dualTextureTexels);

            if (!depthCoverage)
            {
                ReadOnlySpan<float> coverage = [IndexedUiProbeCoverageAboveThreshold, IndexedUiProbeCoverageThreshold,
                    IndexedUiProbeCoverageAboveThreshold, IndexedUiProbeCoverageBelowThreshold];
                MemoryMarshal.AsBytes(coverage).CopyTo(upload.AsSpan(secondaryTextureOffset));
            }
        }
        else
        {
            WriteIndexedUiProbeBatch(constants.Slice(24, 24), -0.5f, true);
            WriteIndexedUiProbeBatch(constants.Slice(48, 24), 0.5f, false);
        }

        for (var vertex = 0; vertex < 8; vertex++)
        {
            var destination =
                upload.AsSpan(vertexOffset + vertex * stride, stride);

            destination[0] = 64;
            destination[1] = 128;
            destination[2] = 192;
            destination[3] = !alphaOnly || vertex < 4 ? (byte)255 : (byte)128;

            if (dualMask)
            {
                var textureCoordinate =
                    MemoryMarshal.Cast<byte, float>(destination.Slice(4, 8));

                textureCoordinate[0] = vertex < 4 ? 0.75f : 0.25f;
                textureCoordinate[1] = 0.5f;
            }
            else if (!alphaOnly)
            {
                destination[6] = vertex < 4 ? (byte)1 : (byte)2;
                destination[7] = vertex < 4 ? (byte)255 : (byte)128;
            }

            var position = MemoryMarshal.Cast<byte, float>(destination[(dualMask ? 12 : alphaOnly ? 4 : 8)..]);
            var rightEdge = (vertex & 2) != 0;

            position[0] = rightEdge ? 1 : -1;

            if (alphaOnly || dualMask)
            {
                var halfCenter = vertex < 4 ? -0.5f : 0.5f;

                position[0] = position[0] * 0.5f + halfCenter;
            }

            position[1] = (vertex & 1) == 0 ? -1 : 1;
        }

        MemoryMarshal.AsBytes(indices).CopyTo(upload.AsSpan(indexOffset));

        if (dualMask)
        {
            upload[textureOffset] = 64;
            upload[textureOffset + 1] = 192;
            upload[secondaryTextureOffset] = 255;
            upload[secondaryTextureOffset + 1] = 128;
        }
        else if (!dualTexture)
        {
            upload[textureOffset + 3] = 64;
            upload[textureOffset + 7] = 192;
        }

        nint texture = 0, secondaryTexture = 0, output = 0, readback = 0, rtvHeap = 0, samplerHeap = 0, dsvHeap = 0;

        try
        {
            var textureDescription = RasterTexture(textureWidth, 1, 0);

            textureDescription.Format = textureFormat;

            CheckHostResult(CreateHostResource(textureDescription, 1, 0, &texture), "indexed UI texture");

            var targetDescription = RasterTexture(width, height, 1);

            targetDescription.Format = colorFormat;

            CheckHostResult(CreateHostResource(targetDescription, 1, 0, &output), "indexed UI target");

            if (alphaOnly || dualMask || dualTexture)
            {
                var secondaryDescription = alphaOnly
                    ? RasterTexture(width, height, 0)
                    : RasterTexture(textureWidth, 1, depthCoverage
                        ? (uint)D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL
                        : 0);

                secondaryDescription.Format = depthCoverage
                    ? HostTextureViewFormats.DepthStencilTypeless
                    : secondaryTextureFormat;
                secondaryDescription.DepthOrArraySize = alphaOnly
                    ? (ushort)2
                    : (ushort)1;

                if (depthCoverage)
                {
                    var descriptorBytes = new ReadOnlySpan<byte>(&secondaryView,
                        sizeof(D3D12_SHADER_RESOURCE_VIEW_DESC)).ToArray();
                    if (!HostTextureViewFormats.TryGetDepthPlaneShaderView(secondaryDescription, descriptorBytes,
                            out var viewFormat) || viewFormat != secondaryTextureFormat)
                    {
                        throw new InvalidOperationException("Indexed UI depth-plane shader view was rejected.");
                    }

                    dsvHeap = CreateShaderViewHeap((nint)_host,
                        (uint)D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV);
                    RequireHostObject(dsvHeap, "indexed UI depth view heap");
                }

                CheckHostResult(CreateHostResource(secondaryDescription, 1, 0, &secondaryTexture),
                    "indexed UI secondary texture");

                RequireHostObject(secondaryTexture, "indexed UI secondary texture");
            }

            CheckHostResult(CreateHostResource(HostBufferDescription(rowPitch * height), 3, CopyDestination, &readback),
                "batched indexed UI readback");

            RequireHostObject(texture, "batched indexed UI texture");
            RequireHostObject(output, "batched indexed UI target");
            RequireHostObject(readback, "batched indexed UI readback");

            rtvHeap = CreateShaderViewHeap((nint)_host, 2);

            if (usesSampler && !CreateIndexedUiHeap(1, 1, out samplerHeap))
            {
                throw new InvalidOperationException("Batched indexed UI sampler heap creation failed.");
            }

            using var utility = AcquireUtilityContext((ulong)upload.Length,
                (uint)IndexedUiCompiler.TextureCount(variant),
                true);

            utility.PrepareVersionedUpload(upload);

            var commands = (ID3D12GraphicsCommandList*)utility.List;
            var textureHeap = (ID3D12DescriptorHeap*)utility.Heap;
            var samplerDescriptors = (ID3D12DescriptorHeap*)samplerHeap;
            var rtv = ((ID3D12DescriptorHeap*)rtvHeap)->GetCPUDescriptorHandleForHeapStart();
            var gpu = ((ID3D12Resource*)utility.Upload)->GetGPUVirtualAddress();

            if (rtv.ptr == 0 || gpu == 0 || (gpu & 255) != 0)
            {
                throw new InvalidOperationException("Batched indexed UI invalid target handle or upload address.");
            }

            if (usesSampler)
            {
                var sampler = new D3D12_SAMPLER_DESC
                {
                    AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                    AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                    AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                    MaxAnisotropy = 1,
                    ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER
                };

                _host->CreateSampler(&sampler, samplerDescriptors->GetCPUDescriptorHandleForHeapStart());
            }

            _host->CreateRenderTargetView((ID3D12Resource*)output, null, rtv);

            var textureHandle = textureHeap->GetCPUDescriptorHandleForHeapStart();

            if (secondaryTexture != 0)
            {
                var secondaryHandle = textureHandle;
                var descriptorSize =
                    _host->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE
                        .D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

                if (alphaOnly)
                {
                    var noiseView = new D3D12_SHADER_RESOURCE_VIEW_DESC
                    {
                        Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
                        ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2DARRAY,
                        Shader4ComponentMapping = IndexedUiProbeComponentMapping
                    };

                    noiseView.Anonymous.Texture2DArray.MipLevels = 1;
                    noiseView.Anonymous.Texture2DArray.ArraySize = 2;

                    _host->CreateShaderResourceView((ID3D12Resource*)secondaryTexture, &noiseView, secondaryHandle);

                    textureHandle.ptr += descriptorSize;
                }
                else
                {
                    secondaryHandle.ptr += descriptorSize;

                    if (dualTexture)
                    {
                        _host->CreateShaderResourceView((ID3D12Resource*)secondaryTexture, &secondaryView,
                            secondaryHandle);
                    }
                    else
                    {
                        _host->CreateShaderResourceView((ID3D12Resource*)secondaryTexture, null, secondaryHandle);
                    }
                }

                if (depthCoverage)
                {
                    ClearIndexedUiProbeDepth(utility.List, secondaryTexture, dsvHeap);
                }
                else
                {
                    TransitionHostResource(utility.List, secondaryTexture, 0, CopyDestination);
                    RecordHostTextureTransfer(utility.List, secondaryTexture, utility.Upload, (ulong)upload.Length,
                        new HostTextureTransferFootprint(0, secondaryTextureFormat,
                            alphaOnly ? width : (uint)textureWidth,
                            alphaOnly ? height : 1U, rowPitch, secondaryTextureOffset), true);

                    TransitionHostResource(utility.List, secondaryTexture, CopyDestination, 0x80);
                }
            }

            if (dualTexture)
            {
                var view = CreateIndexedUiProbeTextureView(textureFormat);
                _host->CreateShaderResourceView((ID3D12Resource*)texture, &view, textureHandle);
            }
            else
            {
                _host->CreateShaderResourceView((ID3D12Resource*)texture, null, textureHandle);
            }


            TransitionHostResource(utility.List, texture, 0, CopyDestination);
            RecordHostTextureTransfer(utility.List, texture, utility.Upload, (ulong)upload.Length,
                new HostTextureTransferFootprint(0, textureFormat, (uint)textureWidth, 1, rowPitch, textureOffset), true);

            TransitionHostResource(utility.List, texture, CopyDestination, 0x80);
            TransitionHostResource(utility.List, output, 0, 4);

            var clear = stackalloc float[4]
            {
                0,
                0,
                0,
                0
            };

            var heaps = stackalloc nint[2]
            {
                utility.Heap,
                samplerHeap

            };

            var viewport =
                new D3D12_VIEWPORT { Width = width, Height = height, MaxDepth = 1 };

            var scissor =
                new RECT { right = width, bottom = height };

            var vertices = new D3D12_VERTEX_BUFFER_VIEW
                { BufferLocation = gpu + vertexOffset, SizeInBytes = (uint)(8 * stride), StrideInBytes = (uint)stride };

            var indexView = new D3D12_INDEX_BUFFER_VIEW
            {
                BufferLocation = gpu + (uint)indexOffset, SizeInBytes = (uint)indices.Length * 2,
                Format = DXGI_FORMAT.DXGI_FORMAT_R16_UINT
            };

            commands->ClearRenderTargetView(rtv, clear, 0, null);
            commands->SetPipelineState((ID3D12PipelineState*)pipeline);
            commands->SetGraphicsRootSignature((ID3D12RootSignature*)root);
            commands->SetDescriptorHeaps(usesSampler ? 2U : 1U, (ID3D12DescriptorHeap**)heaps);
            commands->SetGraphicsRootConstantBufferView(0, gpu);
            commands->SetGraphicsRootConstantBufferView(1, gpu + pixelConstantOffset);
            commands->SetGraphicsRootDescriptorTable(2, textureHeap->GetGPUDescriptorHandleForHeapStart());
            if (usesSampler)
            {
                commands->SetGraphicsRootDescriptorTable(3, samplerDescriptors->GetGPUDescriptorHandleForHeapStart());
            }

            commands->IASetPrimitiveTopology((D3D_PRIMITIVE_TOPOLOGY)4);
            commands->IASetVertexBuffers(0, 1, &vertices);
            commands->IASetIndexBuffer(&indexView);
            commands->RSSetViewports(1, &viewport);
            commands->RSSetScissorRects(1, &scissor);
            commands->OMSetRenderTargets(1, &rtv, false, null);
            commands->DrawIndexedInstanced((uint)indices.Length, 1, 0, 0, 0);
            commands->OMSetRenderTargets(0, null, false, null);

            TransitionHostResource(utility.List, output, 4, CopySource);

            RecordHostTextureTransfer(utility.List, output, readback, rowPitch * height,
                new HostTextureTransferFootprint(0, colorFormat, width, height, rowPitch, 0), false);

            utility.SubmitAndWait(queue);

            if (alphaOnly)
            {
                var pixels =
                    ReadRasterPixels(readback, width, height, rowPitch, 1);

                VerifyIndexedUiProbePixel(pixels.AsSpan(width + 1, 1), [64]);
                VerifyIndexedUiProbePixel(pixels.AsSpan(width + 5, 1), [96]);

                Console.WriteLine(
                    "[graphics-probe] Dithered indexed UI A8 GPU readback passed (two blended alpha values).");
            }
            else if (dualMask)
            {
                var pixels = ReadRasterPixels(readback, width, height, rowPitch);

                VerifyIndexedUiProbePixel(pixels.AsSpan((width + 1) * 4, 4), [64, 128, 192, 192]);
                VerifyIndexedUiProbePixel(pixels.AsSpan((width + 5) * 4, 4), [64, 128, 192, 32]);

                Console.WriteLine(
                    "[graphics-probe] Dual-alpha indexed UI transform rows and mask binding passed GPU readback.");
            }
            else if (dualTexture)
            {
                const int bytesPerTexel = 4;
                const int matchingAboveThreshold = 1;
                const int matchingAtThreshold = 3;
                const int nonmatchingAboveThreshold = 5;
                const int matchingBelowThreshold = 7;

                var pixels = ReadRasterPixels(readback, width, height, rowPitch);
                VerifyIndexedUiProbePixel(pixels.AsSpan((width + matchingAboveThreshold) * bytesPerTexel,
                    bytesPerTexel), [64, 128, 192, 64]);
                VerifyIndexedUiProbePixel(pixels.AsSpan((width + matchingAtThreshold) * bytesPerTexel,
                    bytesPerTexel), [64, 128, 192, 255]);
                VerifyIndexedUiProbePixel(pixels.AsSpan((width + nonmatchingAboveThreshold) * bytesPerTexel,
                    bytesPerTexel), [255, 64, 128, 128]);
                VerifyIndexedUiProbePixel(pixels.AsSpan((width + matchingBelowThreshold) * bytesPerTexel,
                    bytesPerTexel), [255, 64, 128, 128]);

                Console.WriteLine(depthCoverage
                    ? "[graphics-probe] Batched dual-texture indexed UI depth-plane GPU readback passed (packed selection, depth threshold, two batches)."
                    : "[graphics-probe] Batched dual-texture indexed UI GPU readback passed (packed selection, coverage threshold, two batches).");
            }
            else
            {
                var pixels = ReadRasterPixels(readback, width, height, rowPitch);

                VerifyIndexedUiProbePixel(pixels.AsSpan((width + 1) * 4, 4), [64, 128, 192, 64]);
                VerifyIndexedUiProbePixel(pixels.AsSpan((width + 5) * 4, 4), [255, 64, 128, 96]);

                Console.WriteLine(
                    "[graphics-probe] Batched indexed UI GPU readback passed (two batch selectors and two mask alphas).");
            }
        }
        finally
        {
            ReleaseHostObject(dsvHeap);
            ReleaseHostObject(samplerHeap);
            ReleaseHostObject(rtvHeap);
            ReleaseHostObject(readback);
            ReleaseHostObject(output);
            ReleaseHostObject(secondaryTexture);
            ReleaseHostObject(texture);
        }
    }

    private static void ClearIndexedUiProbeDepth(nint list, nint resource, nint viewHeap)
    {
        const uint depthWrite = (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE;
        const uint pixelShaderResource = (uint)D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        const D3D12_CLEAR_FLAGS clearDepth = D3D12_CLEAR_FLAGS.D3D12_CLEAR_FLAG_DEPTH;
        const int thresholdTexel = 1;
        const int belowThresholdTexel = 3;

        var dsv = ((ID3D12DescriptorHeap*)viewHeap)->GetCPUDescriptorHandleForHeapStart();
        var view = new D3D12_DEPTH_STENCIL_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT,
            ViewDimension = D3D12_DSV_DIMENSION.D3D12_DSV_DIMENSION_TEXTURE2D
        };

        _host->CreateDepthStencilView((ID3D12Resource*)resource, &view, dsv);

        var commands = (ID3D12GraphicsCommandList*)list;
        var atThreshold = new RECT { left = thresholdTexel, right = thresholdTexel + 1, bottom = 1 };
        var belowThreshold = new RECT { left = belowThresholdTexel, right = belowThresholdTexel + 1, bottom = 1 };

        TransitionHostResource(list, resource, 0, depthWrite, subresource: HostTextureViewFormats.DepthPlaneSubresource);
        commands->ClearDepthStencilView(dsv, clearDepth, IndexedUiProbeCoverageAboveThreshold, 0, 0, null);
        commands->ClearDepthStencilView(dsv, clearDepth, IndexedUiProbeCoverageThreshold, 0, 1, &atThreshold);
        commands->ClearDepthStencilView(dsv, clearDepth, IndexedUiProbeCoverageBelowThreshold, 0, 1, &belowThreshold);
        TransitionHostResource(list, resource, depthWrite, pixelShaderResource,
            subresource: HostTextureViewFormats.DepthPlaneSubresource);
    }

    private static D3D12_SHADER_RESOURCE_VIEW_DESC CreateIndexedUiProbeTextureView(uint format)
    {
        var view = new D3D12_SHADER_RESOURCE_VIEW_DESC
        {
            Format = (DXGI_FORMAT)format,
            ViewDimension = D3D12_SRV_DIMENSION.D3D12_SRV_DIMENSION_TEXTURE2D,
            Shader4ComponentMapping = IndexedUiProbeComponentMapping
        };
        view.Anonymous.Texture2D.MipLevels = 1;

        return view;
    }

    private static void WriteIndexedUiDualTextureProbeInputs(Span<byte> upload, int pixelOffset, int selectionOffset,
        int width, int height, int textureWidth)
    {
        const float coveredOpacity = 0.25f;
        const byte selectionLowByte = 64;
        const byte selectionHighByte = 128;
        const int selectionByteShift = 8;
        const int blueChannel = 2;
        const int alphaChannel = 3;
        const int nonmatchingSelectionTexel = 2;

        ReadOnlySpan<float> pixel = [IndexedUiProbeCoverageThreshold, coveredOpacity,
            selectionLowByte | (selectionHighByte << selectionByteShift), 0, width, height, textureWidth, 1];
        MemoryMarshal.AsBytes(pixel).CopyTo(upload[pixelOffset..]);

        var selection = upload.Slice(selectionOffset, textureWidth * sizeof(uint));
        for (var texel = 0; texel < textureWidth; texel++)
        {
            var selectionTexel = selection.Slice(texel * sizeof(uint), sizeof(uint));
            selectionTexel[blueChannel] = selectionLowByte;
            selectionTexel[alphaChannel] = texel == nonmatchingSelectionTexel ? (byte)0 : selectionHighByte;
        }
    }

    private static void WriteIndexedUiDualTextureProbeBatch(Span<float> batch, float clipX, bool multiplyColor)
    {
        WriteIndexedUiProbeBatchColor(batch, multiplyColor);
        batch[8] = 0.5f;
        batch[11] = clipX;
        batch[13] = 1;
    }

    private static void WriteIndexedUiProbeBatch(Span<float> batch, float clipX, bool multiplyColor)
    {
        WriteIndexedUiProbeBatchColor(batch, multiplyColor);
        batch[8] = 0.5f;
        batch[11] = 0.5f;
        batch[15] = 0.5f;
        batch[16] = 0.5f;
        batch[19] = clipX;
        batch[21] = 1;
    }

    private static void WriteIndexedUiProbeBatchColor(Span<float> batch, bool multiplyColor)
    {
        if (multiplyColor)
        {
            batch.Slice(4, 4).Fill(1);
        }
        else
        {
            batch[0] = 1;
            batch[1] = 0.25f;
            batch[2] = 0.5f;
            batch[7] = 1;
        }
    }

    private static void VerifyIndexedUiProbePixel(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        for (var channel = 0; channel < expected.Length; channel++)
        {
            if (Math.Abs(actual[channel] - expected[channel]) > 1)
            {
                throw new InvalidOperationException(
                    $"Indexed UI readback mismatch: got {Convert.ToHexString(actual)}, expected {Convert.ToHexString(expected)}.");
            }
        }
    }
}
