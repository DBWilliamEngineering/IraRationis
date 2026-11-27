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
    internal static void VerifyIndexedUiGpu(nint queue)
    {
        VerifyIndexedUiGpu(queue, IndexedUiVariant.BatchedMask);
        VerifyIndexedUiGpu(queue, IndexedUiVariant.Dithered);
        VerifyIndexedUiGpu(queue, IndexedUiVariant.DualAlphaMask);
    }

    private static void VerifyIndexedUiGpu(nint queue, IndexedUiVariant variant)
    {
        var alphaOnly = variant == IndexedUiVariant.Dithered;
        var (name, vertexSource, pixelSource, elements) = IndexedUiProbeProgram(variant);
        var vertex = CompileHostShader(vertexSource, "vs_5_1", Console.Out);
        var pixel = CompileHostShader(pixelSource, "ps_5_1", Console.Out);
        var rootBytes = SerializeIndexedUiRoot(IndexedUiCompiler.TextureCount(variant) == 2);
        if (vertex == null || pixel == null || rootBytes == null)
        {
            throw new InvalidOperationException($"{name} shader or root compilation failed.");
        }

        var template = CreateAlphaVertexFetchProbeState();
        var formats = new D3D12_RT_FORMAT_ARRAY { NumRenderTargets = 1 };
        formats.RTFormats[0] = alphaOnly ? DXGI_FORMAT.DXGI_FORMAT_A8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        var depthFormat = alphaOnly ? DXGI_FORMAT.DXGI_FORMAT_UNKNOWN : DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT_S8X24_UINT;
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
        ], true, true, InputLayout: elements);

        if (!TryPrepareIndexedUiHostState(state, variant, out var hostState, out var colorFormat) ||
            colorFormat != (uint)formats.RTFormats[0])
        {
            throw new InvalidOperationException($"{name} target formats or depth state rejected.");
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
                    $"{name} root");
            }

            RequireHostObject((nint)root, $"{name} root");
            pipeline = CreatePlanarColorPipeline(root, vertex, pixel, hostState, elements, Console.Out, colorFormat);
            RequireHostObject(pipeline, $"{name} pipeline");
            RunIndexedUiGpuDraw(Host(queue), (nint)root, pipeline, variant, colorFormat);
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
            IndexedUiVariant.BatchedMask => ("batched indexed UI", IndexedUiCompiler.BatchedMaskVertexSource,
                IndexedUiCompiler.BatchedMaskPixelSource, [
                    new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0),
                    new VertexInputElement("COLOR", 1, 28, 0, 4, 0, 0),
                    new VertexInputElement("POSITION", 0, 16, 0, 8, 0, 0)
                ]),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
    }

    private static void RunIndexedUiGpuDraw(nint queue, nint root, nint pipeline, IndexedUiVariant variant,
        uint colorFormat)
    {
        const int width = 8;
        const int height = 4;
        const int rowPitch = 256;
        const int vertexOffset = 512;
        const int textureOffset = 1024;
        const int secondaryTextureOffset = 1536;
        var alphaOnly = colorFormat == HostTextureViewFormats.A8Unorm;
        var dualMask = variant == IndexedUiVariant.DualAlphaMask;
        var textureFormat = dualMask ? HostTextureViewFormats.A8Unorm : (uint)DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM;
        var secondaryTextureFormat =
            dualMask ? HostTextureViewFormats.A8Unorm : (uint)DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT;
        var stride = (int)IndexedUiCompiler.VertexStride(variant);
        var indexOffset = vertexOffset + 8 * stride;
        ReadOnlySpan<ushort> indices = [0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7];

        var upload = new byte[alphaOnly ? secondaryTextureOffset + rowPitch * height :
            dualMask ? secondaryTextureOffset + rowPitch : textureOffset + rowPitch];
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
        else
        {
            WriteIndexedUiProbeBatch(constants.Slice(24, 24), -0.5f, true);
            WriteIndexedUiProbeBatch(constants.Slice(48, 24), 0.5f, false);
        }

        for (var vertex = 0; vertex < 8; vertex++)
        {
            var destination = upload.AsSpan(vertexOffset + vertex * stride, stride);
            destination[0] = 64;
            destination[1] = 128;
            destination[2] = 192;
            destination[3] = !alphaOnly || vertex < 4 ? (byte)255 : (byte)128;
            if (dualMask)
            {
                var textureCoordinate = MemoryMarshal.Cast<byte, float>(destination.Slice(4, 8));
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
        else
        {
            upload[textureOffset + 3] = 64;
            upload[textureOffset + 7] = 192;
        }

        nint texture = 0, secondaryTexture = 0, output = 0, readback = 0, rtvHeap = 0, samplerHeap = 0;

        try
        {
            var textureDescription = RasterTexture(2, 1, 0);
            textureDescription.Format = textureFormat;
            CheckHostResult(CreateHostResource(textureDescription, 1, 0, &texture), "indexed UI texture");
            var targetDescription = RasterTexture(width, height, 1);
            targetDescription.Format = colorFormat;
            CheckHostResult(CreateHostResource(targetDescription, 1, 0, &output), "indexed UI target");
            if (alphaOnly || dualMask)
            {
                var secondaryDescription = alphaOnly ? RasterTexture(width, height, 0) : RasterTexture(2, 1, 0);
                secondaryDescription.Format = secondaryTextureFormat;
                secondaryDescription.DepthOrArraySize = alphaOnly ? (ushort)2 : (ushort)1;
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

            if (!CreateIndexedUiHeap(1, 1, out samplerHeap))
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

            var sampler = new D3D12_SAMPLER_DESC
            {
                AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
                MaxAnisotropy = 1,
                ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER
            };

            _host->CreateSampler(&sampler, samplerDescriptors->GetCPUDescriptorHandleForHeapStart());
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
                        Shader4ComponentMapping = 5768
                    };
                    noiseView.Anonymous.Texture2DArray.MipLevels = 1;
                    noiseView.Anonymous.Texture2DArray.ArraySize = 2;
                    _host->CreateShaderResourceView((ID3D12Resource*)secondaryTexture, &noiseView, secondaryHandle);
                    textureHandle.ptr += descriptorSize;
                }
                else
                {
                    secondaryHandle.ptr += descriptorSize;
                    _host->CreateShaderResourceView((ID3D12Resource*)secondaryTexture, null, secondaryHandle);
                }

                TransitionHostResource(utility.List, secondaryTexture, 0, CopyDestination);
                RecordHostTextureTransfer(utility.List, secondaryTexture, utility.Upload, (ulong)upload.Length,
                    new HostTextureTransferFootprint(0, secondaryTextureFormat, alphaOnly ? width : 2U,
                        alphaOnly ? height : 1U, rowPitch, secondaryTextureOffset), true);
                TransitionHostResource(utility.List, secondaryTexture, CopyDestination, 0x80);
            }

            _host->CreateShaderResourceView((ID3D12Resource*)texture, null, textureHandle);
            TransitionHostResource(utility.List, texture, 0, CopyDestination);
            RecordHostTextureTransfer(utility.List, texture, utility.Upload, (ulong)upload.Length,
                new HostTextureTransferFootprint(0, textureFormat, 2, 1, rowPitch, textureOffset), true);
            TransitionHostResource(utility.List, texture, CopyDestination, 0x80);
            TransitionHostResource(utility.List, output, 0, 4);

            var clear = stackalloc float[4] { 0, 0, 0, 0 };
            var heaps = stackalloc nint[2] { utility.Heap, samplerHeap };
            var viewport = new D3D12_VIEWPORT { Width = width, Height = height, MaxDepth = 1 };
            var scissor = new RECT { right = width, bottom = height };
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
            commands->SetDescriptorHeaps(2, (ID3D12DescriptorHeap**)heaps);
            commands->SetGraphicsRootConstantBufferView(0, gpu);
            commands->SetGraphicsRootConstantBufferView(1, gpu + 256);
            commands->SetGraphicsRootDescriptorTable(2, textureHeap->GetGPUDescriptorHandleForHeapStart());
            commands->SetGraphicsRootDescriptorTable(3, samplerDescriptors->GetGPUDescriptorHandleForHeapStart());
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
                var pixels = ReadRasterPixels(readback, width, height, rowPitch, 1);
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
            ReleaseHostObject(samplerHeap);
            ReleaseHostObject(rtvHeap);
            ReleaseHostObject(readback);
            ReleaseHostObject(output);
            ReleaseHostObject(secondaryTexture);
            ReleaseHostObject(texture);
        }
    }

    private static void WriteIndexedUiProbeBatch(Span<float> batch, float clipX, bool multiplyColor)
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

        batch[8] = 0.5f;
        batch[11] = 0.5f;
        batch[15] = 0.5f;
        batch[16] = 0.5f;
        batch[19] = clipX;
        batch[21] = 1;
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
