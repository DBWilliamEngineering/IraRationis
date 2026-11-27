// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Drawing;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int IndexedUiVertexOffset = 512;
    private static readonly NativePipelineCache<GraphicsPipelineMetadata, PlanarColorPipeline> IndexedUiPipelines =
        new("indexed-ui", static pipeline => (pipeline.Root, pipeline.Pipeline));

    private static readonly object SampleSupportGate = new();
    private static readonly Dictionary<uint, bool> SampleSupport = new();
    private static nint _sampleSupportDevice;

    private static bool RejectIndexedUi(string reason)
    {
        Console.Error.WriteLine($"[graphics] indexed-ui REFUSED: {reason}.");

        return false;
    }

    private static bool SupportsUiSampleFormat(uint format)
    {
        lock (SampleSupportGate)
        {
            var device = (nint)_host;
            if (_sampleSupportDevice != device)
            {
                SampleSupport.Clear();
                _sampleSupportDevice = device;
            }

            if (SampleSupport.TryGetValue(format, out var supported))
            {
                return supported;
            }

            var support = new D3D12_FEATURE_DATA_FORMAT_SUPPORT { Format = (DXGI_FORMAT)format };
            var result = (int)_host->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT, &support,
                (uint)sizeof(D3D12_FEATURE_DATA_FORMAT_SUPPORT));
            supported = result >= 0
                        && (support.Support1 & D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE) != 0;
            SampleSupport[format] = supported;

            return supported;
        }
    }

    private static bool TryGetUiArray(TextureSource source)
    {
        var d = ((ResourceObject*)source.Resource)->Description;
        var view = source.Description;
        if (d.Dimension != 3 || d.Width < 64 || d.Height < 64 || d.SampleCount != 1 || d.MipLevels == 0 ||
            d.DepthOrArraySize < 32 || (d.Flags & 8U) != 0
            || !HostTextureViewFormats.TryGetColorCopyFormat(d.Format, out _))
        {
            return false;
        }

        return view is { Length: 40 } && ReadDescriptorUInt32(view, 4) == 5 &&
               HostTextureViewFormats.IsSampledCompatible(d, ReadDescriptorUInt32(view, 0)) &&
               HostTextureViewFormats.IsComponentMapping(ReadDescriptorUInt32(view, 8)) &&
               TextureUploadFormat(ReadDescriptorUInt32(view, 0)) && ReadDescriptorUInt32(view, 16) == 0 &&
               (ReadDescriptorUInt32(view, 20) == uint.MaxValue || (ReadDescriptorUInt32(view, 20) > 0
                                                                    && ReadDescriptorUInt32(view, 20) <= d.MipLevels))
               && ReadDescriptorUInt32(view, 24) <= d.DepthOrArraySize - 32 &&
               ReadDescriptorUInt32(view, 28) >= 32 && ReadDescriptorUInt32(view, 28)
               <= d.DepthOrArraySize - ReadDescriptorUInt32(view, 24) &&
               ReadDescriptorUInt32(view, 32) == 0;
    }

    // Extended console sampler ABI field locations cross-checked against the guest
    // caller and FiveStar's documented ConsoleSamplerDescription. Translate only
    // point/linear/anisotropic and native min/max reduction; reject comparison sampling here.
    private static bool TryTranslateUiSampler(DescriptorMetadata record, out D3D12_SAMPLER_DESC sampler)
    {
        sampler = default;
        if (record.Description is not { } bytes)
        {
            return false;
        }

        if (record.Kind == 22 && bytes.Length == 52 && sizeof(D3D12_SAMPLER_DESC) == 52)
        {
            sampler = MemoryMarshal.Read<D3D12_SAMPLER_DESC>(bytes);
            var filter = (uint)sampler.Filter;
            var basic = filter & ~0x180U;

            return (filter & 0x180U) != 0x80U && basic is 0 or 1 or 4 or 5 or 16 or 17 or 20 or 21 or 0x55 &&
                   (uint)sampler.AddressU is >= 1 and <= 5 &&
                   (uint)sampler.AddressV is >= 1 and <= 5 && (uint)sampler.AddressW is >= 1 and <= 5 &&
                   sampler.MaxAnisotropy <= 16 && float.IsFinite(sampler.MipLODBias) &&
                   float.IsFinite(sampler.MinLOD) && float.IsFinite(sampler.MaxLOD) && sampler.MinLOD <= sampler.MaxLOD;
        }

        if (record.Kind != 61 || bytes.Length != 108 || sizeof(D3D12_SAMPLER_DESC) != 52)
        {
            return false;
        }

        var mag = ReadDescriptorUInt32(bytes, 4);
        var min = ReadDescriptorUInt32(bytes, 8);
        var mip = ReadDescriptorUInt32(bytes, 12);
        var comparison = ReadDescriptorUInt32(bytes, 64);
        var reduction = ReadDescriptorUInt32(bytes, 20);
        var u = ReadDescriptorUInt32(bytes, 24);
        var v = ReadDescriptorUInt32(bytes, 28);
        var w = ReadDescriptorUInt32(bytes, 32);
        if (mag is < 1 or > 3 || min is < 1 or > 3 || mip is < 1 or > 2 || comparison is not (0 or 1) ||
            reduction > 2 || u is < 1 or > 5 || v is < 1 or > 5 || w is < 1 or > 5)
        {
            return false;
        }

        Span<uint> words = stackalloc uint[13];
        words.Clear();
        words[0] = mag == 3 || min == 3 ? 0x55U : ((min - 1) << 4) | ((mag - 1) << 2) | (mip - 1);
        words[0] |= reduction switch { 1 => 0x100U, 2 => 0x180U, _ => 0U };
        words[1] = u;
        words[2] = v;
        words[3] = w;
        words[4] = ReadDescriptorUInt32(bytes, 48);
        words[5] = Math.Clamp(ReadDescriptorUInt32(bytes, 52), 1U, 16U);
        words[6] = 1;
        for (var i = 0; i < 4; i++)
        {
            words[7 + i] = ReadDescriptorUInt32(bytes, 68 + i * 4);
        }

        words[11] = ReadDescriptorUInt32(bytes, 36);
        words[12] = ReadDescriptorUInt32(bytes, 40);
        sampler = MemoryMarshal.Read<D3D12_SAMPLER_DESC>(MemoryMarshal.AsBytes(words));

        return float.IsFinite(sampler.MipLODBias) && sampler.MipLODBias is >= -16 and <= 15.99f &&
               float.IsFinite(sampler.MinLOD) && float.IsFinite(sampler.MaxLOD) && sampler.MinLOD <= sampler.MaxLOD;
    }

    private static void VerifyIndexedUiSampler()
    {
        var bytes = new byte[108];
        var words = MemoryMarshal.Cast<byte, uint>(bytes);
        words[1] = 2;
        words[2] = 1;
        words[3] = 2;
        words[6] = 1;
        words[7] = 3;
        words[8] = 3;
        words[10] = BitConverter.SingleToUInt32Bits(12.0f);
        words[13] = 1;
        words[16] = 1;
        var record = new DescriptorMetadata(61, 0, 0, 0, 0, bytes, null);
        if (!TryTranslateUiSampler(record, out var sampler) || (uint)sampler.Filter != 5 ||
            (uint)sampler.AddressU != 1 || (uint)sampler.AddressV != 3 || sampler.MaxLOD != 12.0f)
        {
            throw new InvalidOperationException("Indexed UI ordinary sampler translation failed.");
        }

        words[5] = 1;
        if (!TryTranslateUiSampler(record, out sampler) || (uint)sampler.Filter != 0x105U)
        {
            throw new InvalidOperationException("Indexed UI minimum reduction sampler rejected.");
        }

        words[5] = 2;
        if (!TryTranslateUiSampler(record, out sampler) || (uint)sampler.Filter != 0x185U)
        {
            throw new InvalidOperationException("Indexed UI maximum reduction sampler rejected.");
        }

        words[5] = 3;
        if (TryTranslateUiSampler(record, out _))
        {
            throw new InvalidOperationException("Indexed UI unsupported reduction sampler accepted.");
        }

        VertexInputElement[] alphaLayout =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("TEXCOORD", 0, 16, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 12, 0, 0)
        ];
        if (!IsIndexedUiLayout(alphaLayout, IndexedUiVariant.AlphaMask) ||
            IsIndexedUiLayout(alphaLayout, IndexedUiVariant.Plain) ||
            IndexedUiCompiler.VertexStride(IndexedUiVariant.AlphaMask) != 20 ||
            IndexedUiCompiler.VertexConstantBytes(IndexedUiVariant.AlphaMask) != 64)
        {
            throw new InvalidOperationException("Indexed UI alpha-mask layout contract failed.");
        }

        alphaLayout[2] = new VertexInputElement("POSITION", 0, 16, 0, 4, 0, 0);
        if (IsIndexedUiLayout(alphaLayout, IndexedUiVariant.AlphaMask))
        {
            throw new InvalidOperationException("Indexed UI invalid position offset accepted.");
        }

        Console.WriteLine("[graphics-probe] Indexed UI alpha-mask layout and sampler translation passed (CPU only).");
    }

    private static bool TryResolveUiSampler(RecordedCommandState state, out D3D12_SAMPLER_DESC sampler,
        out ulong handle, out DescriptorToken token)
    {
        sampler = default;
        handle = 0;
        token = default;
        if (!state.Graphics.Constants.TryGetValue(1, out var words) || words.Length == 0)
        {
            return false;
        }

        foreach (var heap in state.Heaps)
        {
            if (!IsDescriptorHeap(heap))
            {
                continue;
            }

            var d = (DescriptorHeapObject*)heap;
            if (d->Description.Kind != 1 || words[0] >= d->Description.Count)
            {
                continue;
            }

            var offset = (ulong)words[0] * DescriptorStride(1);
            if (d->Start > ulong.MaxValue - offset)
            {
                return false;
            }

            handle = d->Start + offset;
            if (!TryReadMappingRecord(handle, out token) || token.Tag != DescriptorTag || token.Kind is not (22 or 61))
            {
                return false;
            }

            lock (DescriptorGate)
            {
                if (!DescriptorRecords.TryGetValue(token.Id, out var record) || record.Kind != token.Kind)
                {
                    return false;
                }

                if (!TryTranslateUiSampler(record, out sampler))
                {
                    Console.Error.WriteLine(
                        $"[graphics] indexed-ui sampler rejected: index={words[0]}; kind={record.Kind}; desc=" +
                        (record.Description == null ? "default" : Convert.ToHexString(record.Description)));

                    return false;
                }

                return true;
            }
        }

        return false;
    }

    private static byte[]? SerializeIndexedUiRoot(bool dither = true)
    {
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[4];
        var ranges = stackalloc D3D12_DESCRIPTOR_RANGE1[2];
        for (var i = 0; i < 2; i++)
        {
            parameters[i] = new D3D12_ROOT_PARAMETER1 { ParameterType = (D3D12_ROOT_PARAMETER_TYPE)2 };
            parameters[i].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1 { ShaderRegister = (uint)i };
            ranges[i] = new D3D12_DESCRIPTOR_RANGE1
            {
                RangeType = (D3D12_DESCRIPTOR_RANGE_TYPE)(i == 0 ? 0 : 3),
                NumDescriptors = (uint)(i == 0 && dither ? 2 : 1)
            };
            parameters[i + 2] = new D3D12_ROOT_PARAMETER1
                { ParameterType = 0, ShaderVisibility = (D3D12_SHADER_VISIBILITY)5 };
            parameters[i + 2].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
                { NumDescriptorRanges = 1, pDescriptorRanges = ranges + i };
        }

        var desc = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };
        desc.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
            { NumParameters = 4, pParameters = parameters, Flags = (D3D12_ROOT_SIGNATURE_FLAGS)1 };
        var blob = (nint)0;
        var errors = (nint)0;
        try
        {
            var hr = SerializeRootNative(&desc, &blob, &errors);
            if (errors != 0)
            {
                Console.Error.WriteLine(Encoding.UTF8.GetString(ReadShaderBlob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            return hr >= 0 && blob != 0 ? ReadShaderBlob(blob, 1024 * 1024) : null;
        }
        finally
        {
            if (blob != 0)
            {
                _ = HostRelease(blob);
            }

            if (errors != 0)
            {
                _ = HostRelease(errors);
            }
        }
    }

    private static bool IsIndexedUiLayout(VertexInputElement[]? elements, IndexedUiVariant variant)
    {
        if (elements == null || elements.Length != (variant == IndexedUiVariant.AlphaMask ? 3 : 2) ||
            elements[0] != new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0))
        {
            return false;
        }

        return variant == IndexedUiVariant.AlphaMask
            ? elements[1] == new VertexInputElement("TEXCOORD", 0, 16, 0, 4, 0, 0) &&
              elements[2] == new VertexInputElement("POSITION", 0, 16, 0, 12, 0, 0)
            : elements[1] == new VertexInputElement("POSITION", 0, 16, 0, 4, 0, 0);
    }

    private static PlanarColorPipeline? GetIndexedUiPipeline(RecordedCommandState state,
        GraphicsPipelineMetadata metadata, IndexedUiVariant variant)
    {
        if (metadata.Root != state.Graphics.Signature || !IsRootSignature(metadata.Root) ||
            Convert.ToHexString(SHA256.HashData(RootSignatureBlob(metadata.Root))) != IndexedUiCompiler.RootHash ||
            !TextureStripDepthPolicy.TryNormalize(metadata, out var normalized)
            || metadata.InputLayout is not { } elements ||
            !IsIndexedUiLayout(elements, variant))
        {
            return null;
        }

        if (IndexedUiPipelines.Acquire(metadata, state) is { } cached)
        {
            return cached;
        }

        var vs = CompileHostShader(
            variant == IndexedUiVariant.AlphaMask
                ? IndexedUiCompiler.AlphaVertexSource
                : IndexedUiCompiler.VertexSource, "vs_5_1", Console.Error);
        var pixelSource = variant switch
        {
            IndexedUiVariant.Dithered => IndexedUiCompiler.PixelSource,
            IndexedUiVariant.Plain => IndexedUiCompiler.PlainPixelSource,
            IndexedUiVariant.AlphaMask => IndexedUiCompiler.AlphaPixelSource,
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var ps = CompileHostShader(pixelSource, "ps_5_1", Console.Error);
        var blob = SerializeIndexedUiRoot(variant == IndexedUiVariant.Dithered);
        if (vs == null || ps == null || blob == null)
        {
            return null;
        }

        var root = (void*)null;
        var native = (nint)0;
        try
        {
            var iid = RootSignatureId;
            fixed (byte* pointer = blob)
            {
                if (_host->CreateRootSignature(0, pointer, (nuint)blob.Length, &iid, &root) < 0 || root == null)
                {
                    return null;
                }
            }

            native = CreatePlanarColorPipeline(root, vs, ps, normalized, elements, Console.Error);
            if (native == 0)
            {
                return null;
            }

            var result = new PlanarColorPipeline((nint)root, native, false);
            state.HostViews.Add((nint)root);
            state.HostViews.Add(native);
            root = null;
            native = 0;
            IndexedUiPipelines.Publish(metadata, result);

            return result;
        }
        finally
        {
            if (native != 0)
            {
                _ = HostRelease(native);
            }

            if (root != null)
            {
                _ = HostRelease((nint)root);
            }
        }
    }

    private static bool TryRecordIndexedUi(nint self, uint indices, uint instances, uint first, int baseVertex,
        uint firstInstance)
    {
        var state = StateFor(self);
        GraphicsPipelineMetadata? metadata;
        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out metadata);
        }

        if (metadata == null || metadata.Shaders.Count != 2)
        {
            return false;
        }

        var vertexHash = metadata.Shaders.Where(static shader => shader.Stage == 1).Select(static shader => shader.Hash)
            .FirstOrDefault();
        var pixelHash = metadata.Shaders.Where(static shader => shader.Stage == 2).Select(static shader => shader.Hash)
            .FirstOrDefault();
        if (vertexHash == null || pixelHash == null
                               || !IndexedUiCompiler.TryClassify(vertexHash, pixelHash, out var variant))
        {
            return false;
        }

        var dither = variant == IndexedUiVariant.Dithered;
        var stride = IndexedUiCompiler.VertexStride(variant);
        var constantBytes = IndexedUiCompiler.VertexConstantBytes(variant);
        if (!ExecuteGuestVertexFetch || _host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 ||
            state.Topology != 4 || instances != 1 || firstInstance != 0 || indices == 0 || indices % 3 != 0 ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } || state.Targets.Length != 1 ||
            state.Vertices.Count != 1 || !state.Vertices.TryGetValue(0, out var vertex)
            || vertex.FormatOrStride != stride ||
            state.Index is not { } index
            || !TryPlanIndexedUpload(vertex, index, indices, first, baseVertex, out var plan) ||
            state.Graphics.Tables.Count != 1 || !state.Graphics.Tables.TryGetValue(0, out var table) || table == 0
            || table > ulong.MaxValue - 128 ||
            state.Graphics.Views.Count != 0 || state.Graphics.Constants.Count != 1 ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2
            || state.HostViews.Count > MaximumCommandHostViews - 8)
        {
            return RejectIndexedUi("draw state or index/vertex range unsupported");
        }

        plan = plan with { IndexOffset = plan.IndexOffset + 256, Capacity = plan.Capacity + 256 };
        if ((ulong)plan.Capacity > UploadBudget || state.UploadBytes > UploadBudget - (ulong)plan.Capacity)
        {
            return RejectIndexedUi("upload budget");
        }

        TextureSource? noise = null;
        ConstantDescriptor pc = default;
        if (dither && (!TryGetTextureSource(state, table, out noise) || noise == null || !TryGetUiArray(noise) ||
                       !TryGetColorLutConstants(table + 96, 16, out pc)))
        {
            return RejectIndexedUi("dither array or pixel constants unavailable");
        }

        var textureTable = table + (dither ? 32UL : 0UL);
        if (!TryGetTextureSource(state, textureTable, out var colorTexture) || colorTexture == null)
        {
            return RejectIndexedUi($"color descriptor unresolved at 0x{textureTable:X}");
        }

        var colorDescription = ((ResourceObject*)colorTexture.Resource)->Description;
        if (!TryGetTextureView(colorTexture, out var viewFormat))
        {
            return RejectIndexedUi(
                $"color SRV: storage={colorDescription.Format}; view={viewFormat}; dimension={colorDescription.Dimension}; "
                +
                $"extent={colorDescription.Width}x{colorDescription.Height}x{colorDescription.DepthOrArraySize}; mips={colorDescription.MipLevels}; "
                +
                $"layout=0x{colorDescription.Layout:X}; flags=0x{colorDescription.Flags:X}; desc=" +
                (colorTexture.Description == null ? "default" : Convert.ToHexString(colorTexture.Description)));
        }

        if (!SupportsUiSampleFormat(viewFormat))
        {
            return RejectIndexedUi($"host cannot sample color view format={viewFormat}");
        }

        if (!TryGetColorLutConstants(table + (dither ? 64UL : 32UL), constantBytes, out var vc))
        {
            return RejectIndexedUi($"vertex CBV unavailable: expectedBytes={constantBytes}");
        }

        if (!TryResolveUiSampler(state, out var sampler, out var samplerHandle, out var token))
        {
            return RejectIndexedUi(
                $"selected sampler unresolved: handle=0x{samplerHandle:X}; tokenKind={token.Kind}; id={token.Id}");
        }

        var pipeline = GetIndexedUiPipeline(state, metadata, variant);
        if (pipeline == null
            || !TryResolveClear(self, state.Targets[0], 20, out var targetHost, out var rtv, out var format, true)
            || format != 28)
        {
            return RejectIndexedUi("host PSO or UNORM target unavailable");
        }

        state.RetainObject(colorTexture.Resource);
        if (MaterializeHostResource(colorTexture.Resource, out var textureHost) < 0 || textureHost == 0
            || textureHost == targetHost)
        {
            return RejectIndexedUi("native color source materialization or alias");
        }

        PlanarColorTextureBinding[] textures;
        if (noise != null)
        {
            state.RetainObject(noise.Resource);
            if (MaterializeHostResource(noise.Resource, out var noiseHost) < 0 || noiseHost == 0
                                                                               || noiseHost == targetHost
                                                                               || noiseHost == textureHost)
            {
                return RejectIndexedUi("native noise source materialization or alias");
            }

            textures =
            [
                new PlanarColorTextureBinding(noise, noiseHost),
                new PlanarColorTextureBinding(colorTexture, textureHost)
            ];
        }
        else
        {
            textures = [new PlanarColorTextureBinding(colorTexture, textureHost)];
        }

        var target = FindOwnedResource(state, targetHost);
        if (target == 0)
        {
            return RejectIndexedUi("target ownership");
        }

        var heap = (nint)0;
        var samplers = (nint)0;
        var upload = (nint)0;
        try
        {
            if (!CreateIndexedUiHeap(0, (uint)textures.Length, out heap) || !CreateIndexedUiHeap(1, 1, out samplers) ||
                CreateHostResource(HostBufferDescription((ulong)plan.Capacity), 2, GenericRead, &upload) < 0
                || upload == 0)
            {
                return RejectIndexedUi("native descriptor or upload allocation");
            }

            var srvHeap = (ID3D12DescriptorHeap*)heap;
            var samplerHeap = (ID3D12DescriptorHeap*)samplers;
            var cpu = srvHeap->GetCPUDescriptorHandleForHeapStart();
            foreach (var texture in textures)
            {
                WriteHostTextureShaderView(texture.Source, texture.Host, cpu);
                cpu.ptr += _host->GetDescriptorHandleIncrementSize(0);
            }

            sampler.MaxAnisotropy = Math.Clamp(sampler.MaxAnisotropy, 1U, 16U);
            _host->CreateSampler(&sampler, samplerHeap->GetCPUDescriptorHandleForHeapStart());
            var gpu = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();
            var srv = srvHeap->GetGPUDescriptorHandleForHeapStart();
            var samplerGpu = samplerHeap->GetGPUDescriptorHandleForHeapStart();
            if (gpu == 0 || (gpu & 255) != 0 || srv.ptr == 0 || samplerGpu.ptr == 0)
            {
                return RejectIndexedUi("native GPU addresses");
            }

            var deferred = new IndexedUiUpload(plan, vertex.Address, table, textures, vc, pc, samplerHandle, token.Id,
                token.Kind,
                upload, target, ((ResourceObject*)target)->Generation, variant);
            state.HostViews.Add(heap);
            state.HostViews.Add(samplers);
            state.HostViews.Add(upload);
            heap = samplers = upload = 0;
            state.UploadBytes += (ulong)plan.Capacity;
            SealMemoryBoundary(self, state, null, null, indexedUi: new IndexedUiBoundary(deferred, false));
            var list = Host(self);
            var commands = (ID3D12GraphicsCommandList*)list;
            var heaps = stackalloc nint[2];
            heaps[0] = (nint)srvHeap;
            heaps[1] = (nint)samplerHeap;
            var nativeRtv = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = rtv };
            var vb = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = gpu + IndexedUiVertexOffset, SizeInBytes = (uint)plan.VertexBytes,
                StrideInBytes = stride
            };
            var ib = new D3D12_INDEX_BUFFER_VIEW
            {
                BufferLocation = gpu + (uint)plan.IndexOffset, SizeInBytes = (uint)plan.IndexBytes,
                Format = (DXGI_FORMAT)plan.IndexFormat
            };
            TransitionHostResource(list, targetHost, 0, 4);
            TransitionPlanarColorTextures(list, textures, 0, 0x80);
            commands->SetDescriptorHeaps(2, (ID3D12DescriptorHeap**)heaps);
            commands->SetPipelineState((ID3D12PipelineState*)pipeline.Pipeline);
            commands->SetGraphicsRootSignature((ID3D12RootSignature*)pipeline.Root);
            commands->SetGraphicsRootConstantBufferView(0, gpu);
            commands->SetGraphicsRootConstantBufferView(1, gpu + 256);
            commands->SetGraphicsRootDescriptorTable(2, srv);
            commands->SetGraphicsRootDescriptorTable(3, samplerGpu);
            commands->IASetVertexBuffers(0, 1, &vb);
            commands->IASetIndexBuffer(&ib);
            commands->OMSetRenderTargets(1, &nativeRtv, false, null);
            TraceHostCommand(self, "indexed-ui DrawIndexedInstanced");
            commands->DrawIndexedInstanced(indices, 1, 0, baseVertex, 0);
            commands->OMSetRenderTargets(0, null, false, null);
            TransitionPlanarColorTextures(list, textures, 0x80, 0);
            TransitionHostResource(list, targetHost, 4, 0);
            SealMemoryBoundary(self, state, null, null, indexedUi: new IndexedUiBoundary(deferred, true));
            TraceGraphicsSuccess(
                $"[graphics] indexed-ui RECORDED: indices={indices}; stride={stride}; samplerKind={token.Kind}; dither={dither}; variant={variant}.");

            return true;
        }
        finally
        {
            if (heap != 0)
            {
                _ = HostRelease(heap);
            }

            if (samplers != 0)
            {
                _ = HostRelease(samplers);
            }

            if (upload != 0)
            {
                _ = HostRelease(upload);
            }
        }
    }

    private static bool CreateIndexedUiHeap(uint kind, uint count, out nint heap)
    {
        var desc = new DescriptorHeapDescription { Kind = kind, Count = count, Flags = 1 };
        var iid = DescriptorHeapId;
        var native = (nint)0;
        var result =
            ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot((nint)_host, 14))(
                (nint)_host, &desc, &iid, &native);
        heap = native;

        return result >= 0 && heap != 0;
    }

    private static void ExecuteIndexedUiBoundary(nint queue, IndexedUiBoundary boundary,
        OrderedTextureUploads? ordered = null)
    {
        var upload = boundary.Upload;
        if (boundary.Complete)
        {
            if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
            {
                Environment.FailFast("[graphics] indexed-ui completion without preparation.");
            }

            MarkGpuWrite(upload.Target, queue);
            Interlocked.Increment(ref _draws);
            TraceGraphicsSuccess(
                $"[graphics] indexed-ui GPU COMPLETED: dither={upload.Dither}; variant={upload.Variant} (visual content unverified).");
            TraceColor(queue, upload.Target, "indexed-ui-output");

            return;
        }

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0 || !IsResource(upload.Target) ||
            ((ResourceObject*)upload.Target)->Generation != upload.Generation
            || !upload.Textures.All(static t => IsTextureSourceCurrent(t.Source)) ||
            !TryReadMappingRecord(upload.SamplerHandle, out DescriptorToken sampler) || sampler.Tag != DescriptorTag ||
            sampler.Id != upload.SamplerToken || sampler.Kind != upload.SamplerKind ||
            !TryGetColorLutConstants(upload.Table + (upload.Dither ? 64UL : 32UL),
                IndexedUiCompiler.VertexConstantBytes(upload.Variant), out var vc)
            || !IsSameColorLutConstant(vc, upload.VertexConstants) ||
            (upload.Dither && (!TryGetColorLutConstants(upload.Table + 96, 16, out var pc)
                               || !IsSameColorLutConstant(pc, upload.PixelConstants))))
        {
            Environment.FailFast("[graphics] indexed-ui submission source changed or invalid.");
        }

        var plan = upload.Plan;
        var rented = ArrayPool<byte>.Shared.Rent(plan.Capacity);
        try
        {
            var snapshot = rented.AsSpan(0, plan.Capacity);
            snapshot.Clear();
            var indices = snapshot.Slice(plan.IndexOffset, plan.IndexBytes);
            if (!TryReadGuestMemory(plan.IndexAddress, indices)
                || !TryGetVertexRange(plan, indices, out var offset, out var bytes) ||
                !TryReadGuestMemory(upload.Vertex + (ulong)offset,
                    snapshot.Slice(IndexedUiVertexOffset + offset, bytes)) ||
                !TryReadGuestMemory(upload.VertexConstants.Address,
                    snapshot[..IndexedUiCompiler.VertexConstantBytes(upload.Variant)]) ||
                (upload.Dither && !TryReadGuestMemory(upload.PixelConstants.Address, snapshot.Slice(256, 16))))
            {
                Environment.FailFast("[graphics] indexed-ui vertex/index/constant snapshot refused.");
            }

            MapPlanarColorUpload(upload.Host, snapshot);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        PreparePlanarColorTextures(queue, upload.Textures, false, ordered);
        TraceGraphicsSuccess("[graphics] indexed-ui PREPARED.");
    }
}
