// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
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
using IraRationis.Graphics.Textures;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int IndexedUiConstantAlignment = 256;
    private const int IndexedUiBatchSelectorOffset = 6;
    private const ulong IndexedUiTextureDescriptorBytes = 32;
    private const ulong IndexedUiPixelConstantTableOffset = 96;
    private const uint IndexedUiTriangleListTopology = 4;
    private const int TriangleIndexCount = 3;
    private const string IndexedUiVertexShaderProfile = "vs_5_1";
    private const string IndexedUiPixelShaderProfile = "ps_5_1";
    private const int MaximumRecordedIndexedUiPipelines = 64;
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<nint, IndexedUiPipeline>>
        IndexedUiRecordingPipelines = new();
    private static readonly NativePipelineCache<GraphicsPipelineMetadata, IndexedUiPipeline> SharedIndexedUiPipelines =
        new("indexed-ui", static pipeline => (pipeline.Root, pipeline.Pipeline));

    private static readonly object TextureAccessSupportGate = new();
    private static readonly Dictionary<uint, D3D12_FORMAT_SUPPORT1> TextureAccessSupport = new();
    private static nint _textureAccessSupportDevice;

    private static bool RejectIndexedUi(string reason)
    {
        Console.Error.WriteLine($"[graphics] indexed-ui REFUSED: {reason}.");

        return false;
    }

    private static bool SupportsHostTextureSampling(uint format)
    {
        return SupportsHostTextureAccess(format, D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE);
    }

    private static bool SupportsHostTextureAccess(uint format, D3D12_FORMAT_SUPPORT1 requiredSupport)
    {
        lock (TextureAccessSupportGate)
        {
            var device = (nint)_host;

            if (_textureAccessSupportDevice != device)
            {
                TextureAccessSupport.Clear();
                _textureAccessSupportDevice = device;
            }

            if (!TextureAccessSupport.TryGetValue(format, out var supported))
            {
                var support = new D3D12_FEATURE_DATA_FORMAT_SUPPORT { Format = (DXGI_FORMAT)format };
                var result = (int)_host->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT, &support,
                    (uint)sizeof(D3D12_FEATURE_DATA_FORMAT_SUPPORT));
                supported = result >= 0 ? support.Support1 : 0;
                TextureAccessSupport[format] = supported;
            }

            return (supported & requiredSupport) == requiredSupport;
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

    private static void VerifyIndexedUiDualAlpha()
    {
        VertexInputElement[] layout =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("TEXCOORD", 0, 16, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 12, 0, 0)
        ];

        const IndexedUiVariant dualAlpha = IndexedUiVariant.DualAlphaMask;
        if (!IndexedUiCompiler.TryClassify(IndexedUiCompiler.DualAlphaVertexHash, IndexedUiCompiler.DualAlphaPixelHash,
                out var variant) || variant != dualAlpha ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.DualAlphaVertexHash, IndexedUiCompiler.AlphaPixelHash,
                out _) ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.AlphaVertexHash, IndexedUiCompiler.DualAlphaPixelHash,
                out _) ||
            IndexedUiCompiler.VertexStride(dualAlpha) != 20 || IndexedUiCompiler.VertexConstantBytes(dualAlpha) != 96 ||
            IndexedUiVertexConstantsOffset(dualAlpha) != 64 || !IsIndexedUiLayout(layout, dualAlpha))
        {
            throw new InvalidOperationException("Indexed UI dual alpha mask input or binding contract failed.");
        }

        layout[1] = new VertexInputElement("TEXCOORD", 0, 16, 0, 8, 0, 0);
        if (IsIndexedUiLayout(layout, dualAlpha))
        {
            throw new InvalidOperationException(
                "Indexed UI dual alpha mask accepted an invalid texture coordinate offset.");
        }

        if (CompileHostShader(IndexedUiCompiler.DualAlphaVertexSource, IndexedUiVertexShaderProfile, Console.Out) ==
            null ||
            CompileHostShader(IndexedUiCompiler.DualAlphaPixelSource, IndexedUiPixelShaderProfile, Console.Out) == null ||
            SerializeIndexedUiRoot(true) == null)
        {
            throw new InvalidOperationException(
                "Indexed UI dual alpha mask host shader compilation or root serialization failed.");
        }

        Console.WriteLine(
            "[graphics-probe] Indexed UI dual alpha mask layout, bindings, host shaders and root serialization passed (no GPU draw).");
    }

    private static void VerifyIndexedUiBatchedMask()
    {
        const IndexedUiVariant batchedMask = IndexedUiVariant.BatchedMask;
        VertexInputElement[] layout =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("COLOR", 1, 28, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 8, 0, 0)
        ];

        if (!IndexedUiCompiler.TryClassify(IndexedUiCompiler.BatchedMaskVertexHash,
                IndexedUiCompiler.BatchedMaskPixelHash,
                out var variant) || variant != batchedMask ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.BatchedMaskVertexHash, IndexedUiCompiler.AlphaPixelHash,
                out _) ||
            IndexedUiCompiler.VertexStride(batchedMask) != 16 ||
            !IsIndexedUiLayout(layout, batchedMask) ||
            IndexedUiVertexConstantsOffset(batchedMask) != 32 ||
            IndexedUiVertexConstantBytes(batchedMask, 2304) != 2304 ||
            IndexedUiVertexConstantBytes(batchedMask, 256) != 192 ||
            IndexedUiVertexConstantBytes(batchedMask, 65536) != 24576)
        {
            throw new InvalidOperationException("Indexed UI batched mask layout or binding contract failed.");
        }

        VerifyIndexedUiBatchSelectors(batchedMask, 1530.06f);

        if (CompileHostShader(IndexedUiCompiler.BatchedMaskVertexSource, IndexedUiVertexShaderProfile, Console.Out) ==
            null ||
            CompileHostShader(IndexedUiCompiler.BatchedMaskPixelSource, IndexedUiPixelShaderProfile, Console.Out) == null ||
            SerializeIndexedUiRoot(false) == null)
        {
            throw new InvalidOperationException(
                "Indexed UI batched mask host shader compilation or root serialization failed.");
        }

        Console.WriteLine(
            "[graphics-probe] Indexed UI batched mask layout, bindings, selectors, host shaders and root serialization passed (no GPU draw).");
    }

    private static void VerifyIndexedUiBatchedDualTexture()
    {
        const IndexedUiVariant expectedVariant = IndexedUiVariant.BatchedDualTexture;
        VertexInputElement[] layout =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("COLOR", 1, 28, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 8, 0, 0)
        ];

        if (!IndexedUiCompiler.TryClassify(IndexedUiCompiler.BatchedDualTextureVertexHash,
                IndexedUiCompiler.BatchedDualTexturePixelHash, out var variant) || variant != expectedVariant ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.BatchedDualTextureVertexHash,
                IndexedUiCompiler.BatchedMaskPixelHash, out _) ||
            IndexedUiCompiler.VertexStride(variant) != 16 || !IsIndexedUiLayout(layout, variant) ||
            IndexedUiCompiler.TextureCount(variant) != 2 || IndexedUiVertexConstantsOffset(variant) != 64 ||
            IndexedUiCompiler.VertexConstantBytes(variant) != 64 || IndexedUiCompiler.PixelConstantBytes(variant) != 32 ||
            IndexedUiVertexConstantBytes(variant, 1536) != 1536 ||
            IndexedUiVertexConstantBytes(variant, 1535) != 1472 ||
            IndexedUiVertexConstantBytes(variant, 65536) != 16384 || IndexedUiUsesSampler(variant))
        {
            throw new InvalidOperationException("Indexed UI batched dual texture layout or binding contract failed.");
        }

        VerifyIndexedUiBatchSelectors(variant, 1020.04f);

        if (CompileHostShader(IndexedUiCompiler.BatchedDualTextureVertexSource, IndexedUiVertexShaderProfile,
                Console.Out) == null ||
            CompileHostShader(IndexedUiCompiler.BatchedDualTexturePixelSource, IndexedUiPixelShaderProfile,
                Console.Out) == null ||
            SerializeIndexedUiRoot(twoTextures: true, usesSampler: false) == null)
        {
            throw new InvalidOperationException(
                "Indexed UI batched dual texture shader compilation or root serialization failed.");
        }

        Console.WriteLine(
            "[graphics-probe] Indexed UI batched dual texture layout, bindings, selectors, host shaders and root serialization passed (no GPU draw).");
    }

    private static void VerifyIndexedUiBatchSelectors(IndexedUiVariant variant, float selectorScale)
    {
        const int vectorBytes = 4 * sizeof(float);
        var batchBytes = IndexedUiCompiler.BatchConstantBytes(variant);
        var rowsPerBatch = batchBytes / vectorBytes;

        for (var batchIndex = 0; batchIndex < IndexedUiCompiler.BatchedMaskMaximumBatches; batchIndex++)
        {
            for (var row = 0; row < rowsPerBatch; row++)
            {
                var shaderIndex = (int)MathF.FusedMultiplyAdd(batchIndex / 255f, selectorScale, row + 0.1f);

                if (shaderIndex != batchIndex * rowsPerBatch + row)
                {
                    throw new InvalidOperationException($"Indexed UI {variant} UNORM selector conversion differs.");
                }
            }
        }

        const int stride = 16;
        const int largestReferencedSelector = 23;
        var constantBytes = (largestReferencedSelector + 1) * batchBytes;
        var plan = new IndexedUploadPlan(0x2000, 57, 5 * stride, stride, 0, 6, 6, -3);
        var vertices = new byte[3 * stride];
        vertices[IndexedUiBatchSelectorOffset] = 1;
        vertices[stride + IndexedUiBatchSelectorOffset] = byte.MaxValue;
        vertices[2 * stride + IndexedUiBatchSelectorOffset] = largestReferencedSelector;

        if (!AreIndexedUiBatchesPresent(plan, [4, 0, 6, 0, 4, 0], vertices, stride, constantBytes, batchBytes) ||
            !AreIndexedUiBatchesPresent(plan with { IndexFormat = 42, IndexBytes = 12 },
                [4, 0, 0, 0, 6, 0, 0, 0, 4, 0, 0, 0], vertices, stride, constantBytes, batchBytes) ||
            AreIndexedUiBatchesPresent(plan, [4, 0, 5, 0, 6, 0], vertices, stride, constantBytes, batchBytes) ||
            AreIndexedUiBatchesPresent(plan, [4, 0, 6, 0, 4, 0], vertices, stride, constantBytes - batchBytes, batchBytes))
        {
            throw new InvalidOperationException($"Indexed UI {variant} selected constants outside the referenced range.");
        }
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

    private static byte[]? SerializeIndexedUiRoot(bool twoTextures = true, bool usesSampler = true)
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
                NumDescriptors = (uint)(i == 0 && twoTextures ? 2 : 1)
            };
            parameters[i + 2] = new D3D12_ROOT_PARAMETER1
                { ParameterType = 0, ShaderVisibility = (D3D12_SHADER_VISIBILITY)5 };
            parameters[i + 2].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
                { NumDescriptorRanges = 1, pDescriptorRanges = ranges + i };
        }

        var desc = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };
        desc.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
            { NumParameters = usesSampler ? 4U : 3U, pParameters = parameters, Flags = (D3D12_ROOT_SIGNATURE_FLAGS)1 };
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
        if (IndexedUiCompiler.BatchConstantBytes(variant) != 0)
        {
            return elements is { Length: 3 } &&
                   elements[0] == new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0) &&
                   elements[1] == new VertexInputElement("COLOR", 1, 28, 0, 4, 0, 0) &&
                   elements[2] == new VertexInputElement("POSITION", 0, 16, 0, 8, 0, 0);
        }

        var hasTextureCoordinate = variant is IndexedUiVariant.AlphaMask or IndexedUiVariant.DualAlphaMask;

        if (elements == null || elements.Length != (hasTextureCoordinate ? 3 : 2) ||
            elements[0] != new VertexInputElement("COLOR", 0, 28, 0, 0, 0, 0))
        {
            return false;
        }

        return hasTextureCoordinate
            ? elements[1] == new VertexInputElement("TEXCOORD", 0, 16, 0, 4, 0, 0) &&
              elements[2] == new VertexInputElement("POSITION", 0, 16, 0, 12, 0, 0)
            : elements[1] == new VertexInputElement("POSITION", 0, 16, 0, 4, 0, 0);
    }

    private static void VerifyIndexedUiTransformedTexture()
    {
        const IndexedUiVariant expectedVariant = IndexedUiVariant.TransformedTexture;
        VertexInputElement[] layout =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("POSITION", 0, 16, 0, 4, 0, 0)
        ];

        if (!IndexedUiCompiler.TryClassify(IndexedUiCompiler.TransformedTextureVertexHash,
                IndexedUiCompiler.TransformedTexturePixelHash, out var variant) || variant != expectedVariant ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.TransformedTextureVertexHash,
                IndexedUiCompiler.PlainPixelHash, out _) ||
            IndexedUiCompiler.VertexStride(variant) != 12 || IndexedUiCompiler.VertexConstantBytes(variant) != 64 ||
            IndexedUiCompiler.TextureCount(variant) != 1 || IndexedUiVertexConstantsOffset(variant) != 32 ||
            !IsIndexedUiLayout(layout, variant))
        {
            throw new InvalidOperationException("Transformed indexed UI shader binding or input layout differs.");
        }

        if (CompileHostShader(IndexedUiCompiler.TransformedTextureVertexSource, IndexedUiVertexShaderProfile,
                Console.Out) == null ||
            CompileHostShader(IndexedUiCompiler.TransformedTexturePixelSource, IndexedUiPixelShaderProfile,
                Console.Out) == null ||
            SerializeIndexedUiRoot(false) == null)
        {
            throw new InvalidOperationException(
                "Transformed indexed UI shaders or root were rejected by the host compiler.");
        }

        Console.WriteLine(
            "[graphics-probe] Transformed indexed UI layout, bindings, host shaders and root serialization passed (no GPU draw).");
    }

    private static ulong IndexedUiVertexConstantsOffset(IndexedUiVariant variant)
    {
        return (ulong)IndexedUiCompiler.TextureCount(variant) * IndexedUiTextureDescriptorBytes;
    }

    private static bool IndexedUiUsesSampler(IndexedUiVariant variant)
    {
        return variant != IndexedUiVariant.BatchedDualTexture;
    }

    private static int IndexedUiVertexConstantBytes(IndexedUiVariant variant, uint descriptorSize)
    {
        var batchBytes = IndexedUiCompiler.BatchConstantBytes(variant);

        if (batchBytes == 0)
        {
            return IndexedUiCompiler.VertexConstantBytes(variant);
        }

        var batches = Math.Min(descriptorSize / (uint)batchBytes,
            IndexedUiCompiler.BatchedMaskMaximumBatches);

        return (int)batches * batchBytes;
    }

    private static bool AreIndexedUiBatchesPresent(IndexedUploadPlan plan, ReadOnlySpan<byte> indices,
        ReadOnlySpan<byte> referencedVertices, int firstVertexByte, int constantBytes, int batchBytes)
    {
        var batchCount = constantBytes / batchBytes;

        if (batchCount >= IndexedUiCompiler.BatchedMaskMaximumBatches)
        {
            return true;
        }

        var indexSize = plan.IndexFormat == 57 ? sizeof(ushort) : sizeof(uint);

        for (var indexOffset = 0; indexOffset < indices.Length; indexOffset += indexSize)
        {
            var index = indexSize == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16LittleEndian(indices[indexOffset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(indices[indexOffset..]);
            var vertexByte = (index + plan.BaseVertex) * plan.VertexStride - firstVertexByte;
            var selector = vertexByte + IndexedUiBatchSelectorOffset;

            if (selector < 0 || selector >= referencedVertices.Length
                             || referencedVertices[(int)selector] >= batchCount)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryPrepareIndexedUiHostState(GraphicsPipelineMetadata metadata, IndexedUiVariant variant,
        out GraphicsPipelineMetadata normalized, out uint colorFormat)
    {
        colorFormat = 0;

        if (!TextureStripDepthPolicy.TryNormalize(metadata, out normalized) ||
            metadata.State.FirstOrDefault(static record => record.Kind == 15) is not { Bytes.Length: 40 } targets)
        {
            return false;
        }

        colorFormat = BinaryPrimitives.ReadUInt32LittleEndian(targets.Bytes.AsSpan(4));
        if (colorFormat == HostTextureViewFormats.A8Unorm &&
            (variant != IndexedUiVariant.Dithered || !ReferenceEquals(normalized, metadata)))
        {
            return false;
        }

        return IsPipelineStateCompatible(targets, false, true, colorFormat);
    }

    private static IndexedUiPipeline? GetIndexedUiPipeline(RecordedCommandState state,
        GraphicsPipelineMetadata metadata, IndexedUiVariant variant)
    {
        if (metadata.Root != state.Graphics.Signature || !IsRootSignature(metadata.Root))
        {
            return null;
        }

        var cache = IndexedUiRecordingPipelines.GetOrCreateValue(state);

        if (cache.TryGetValue(state.Pipeline, out var cached))
        {
            return cached;
        }

        var canCache = cache.Count < MaximumRecordedIndexedUiPipelines;

        if (SharedIndexedUiPipelines.Acquire(metadata, state) is { } shared)
        {
            if (canCache)
            {
                cache.Add(state.Pipeline, shared);
            }

            return shared;
        }

        if (Convert.ToHexString(SHA256.HashData(RootSignatureBlob(metadata.Root))) != IndexedUiCompiler.RootHash ||
            !TryPrepareIndexedUiHostState(metadata, variant, out var normalized, out var colorFormat) ||
            metadata.InputLayout is not { } elements ||
            !IsIndexedUiLayout(elements, variant))
        {
            return null;
        }

        var vertexSource = variant switch
        {
            IndexedUiVariant.AlphaMask => IndexedUiCompiler.AlphaVertexSource,
            IndexedUiVariant.DualAlphaMask => IndexedUiCompiler.DualAlphaVertexSource,
            IndexedUiVariant.BatchedMask => IndexedUiCompiler.BatchedMaskVertexSource,
            IndexedUiVariant.BatchedDualTexture => IndexedUiCompiler.BatchedDualTextureVertexSource,
            IndexedUiVariant.TransformedTexture => IndexedUiCompiler.TransformedTextureVertexSource,
            _ => IndexedUiCompiler.VertexSource
        };
        var pixelSource = variant switch
        {
            IndexedUiVariant.Dithered => IndexedUiCompiler.PixelSource,
            IndexedUiVariant.Plain => IndexedUiCompiler.PlainPixelSource,
            IndexedUiVariant.AlphaMask => IndexedUiCompiler.AlphaPixelSource,
            IndexedUiVariant.DualAlphaMask => IndexedUiCompiler.DualAlphaPixelSource,
            IndexedUiVariant.BatchedMask => IndexedUiCompiler.BatchedMaskPixelSource,
            IndexedUiVariant.BatchedDualTexture => IndexedUiCompiler.BatchedDualTexturePixelSource,
            IndexedUiVariant.TransformedTexture => IndexedUiCompiler.TransformedTexturePixelSource,
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        var vs = CompileHostShader(vertexSource, IndexedUiVertexShaderProfile, Console.Error);
        var ps = CompileHostShader(pixelSource, IndexedUiPixelShaderProfile, Console.Error);
        var blob = SerializeIndexedUiRoot(IndexedUiCompiler.TextureCount(variant) == 2,
            IndexedUiUsesSampler(variant));
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

            native = CreatePlanarColorPipeline(root, vs, ps, normalized, elements, Console.Error, colorFormat);
            if (native == 0)
            {
                return null;
            }

            var result = new IndexedUiPipeline((nint)root, native, colorFormat);
            _ = state.HostViews.EnsureCapacity(state.HostViews.Count + 2);
            state.HostViews.Add((nint)root);
            state.HostViews.Add(native);
            root = null;
            native = 0;

            if (canCache)
            {
                cache.Add(state.Pipeline, result);
            }

            SharedIndexedUiPipelines.Publish(metadata, result);

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

        var vertexShader = metadata.Shaders[0];
        var pixelShader = metadata.Shaders[1];

        if (vertexShader.Stage == 2)
        {
            (vertexShader, pixelShader) = (pixelShader, vertexShader);
        }

        if (vertexShader.Stage != 1 || pixelShader.Stage != 2 ||
            !IndexedUiCompiler.TryClassify(vertexShader.Hash, pixelShader.Hash, out var variant))
        {
            return false;
        }

        var dither = variant == IndexedUiVariant.Dithered;
        var dualAlpha = variant == IndexedUiVariant.DualAlphaMask;
        var usesSampler = IndexedUiUsesSampler(variant);
        var requiredTextureSupport = usesSampler
            ? D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE
            : D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_LOAD;
        var stride = IndexedUiCompiler.VertexStride(variant);
        var minimumConstantBytes = IndexedUiCompiler.VertexConstantBytes(variant);
        var pixelConstantBytes = IndexedUiCompiler.PixelConstantBytes(variant);
        if (!ExecuteGuestVertexFetch || _host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 ||
            state.Topology != IndexedUiTriangleListTopology || instances != 1 || firstInstance != 0 || indices == 0 ||
            indices % TriangleIndexCount != 0 ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } || state.Targets.Length != 1 ||
            state.Vertices.Count != 1 || !state.Vertices.TryGetValue(0, out var vertex)
            || vertex.FormatOrStride != stride ||
            state.Index is not { } index
            || !TryPlanIndexedUpload(vertex, index, indices, first, baseVertex, out var plan) ||
            state.Graphics.Tables.Count != 1 || !state.Graphics.Tables.TryGetValue(0, out var table) || table == 0
            || table > ulong.MaxValue - 128 ||
            state.Graphics.Views.Count != 0 || (usesSampler && state.Graphics.Constants.Count != 1) ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2
            || state.HostViews.Count > MaximumCommandHostViews - 8)
        {
            return RejectIndexedUi("draw state or index/vertex range unsupported");
        }

        TextureSource? additionalTexture = null;
        uint? secondaryDepthPlane = null;
        ConstantDescriptor pc = default;

        if (pixelConstantBytes != 0 && !TryReadConstantDescriptor(table + IndexedUiPixelConstantTableOffset,
                pixelConstantBytes, out pc))
        {
            return RejectIndexedUi($"pixel CBV unavailable: expectedBytes={pixelConstantBytes}");
        }

        if (dither && (!TryGetTextureSource(state, table, out additionalTexture) || additionalTexture == null
                || !TryGetUiArray(additionalTexture)))
        {
            return RejectIndexedUi("dither array unavailable");
        }

        if (dualAlpha || variant == IndexedUiVariant.BatchedDualTexture)
        {
            if (!TryGetTextureSource(state, table + IndexedUiTextureDescriptorBytes, out additionalTexture) ||
                additionalTexture == null)
            {
                return RejectIndexedUi(
                    $"{variant} secondary texture descriptor unresolved at table byte {IndexedUiTextureDescriptorBytes}");
            }

            var secondaryStorage = ((ResourceObject*)additionalTexture.Resource)->Description;
            if (variant == IndexedUiVariant.BatchedDualTexture &&
                HostTextureViewFormats.TryGetDepthPlaneShaderView(secondaryStorage, additionalTexture.Description,
                    out var secondaryFormat))
            {
                secondaryDepthPlane = HostTextureViewFormats.DepthPlaneSubresource;
            }
            else if (!TryGetTextureView(additionalTexture, out secondaryFormat))
            {
                return RejectIndexedUi(DescribeIndexedUiTextureView("secondary", additionalTexture, secondaryFormat));
            }

            if (!SupportsHostTextureAccess(secondaryFormat, requiredTextureSupport))
            {
                return RejectIndexedUi($"host secondary view format={secondaryFormat} does not support " +
                                       $"{requiredTextureSupport}; descriptor={additionalTexture.Token}");
            }
        }

        var textureTable = table + (dither ? IndexedUiTextureDescriptorBytes : 0UL);
        if (!TryGetTextureSource(state, textureTable, out var colorTexture) || colorTexture == null)
        {
            return RejectIndexedUi($"color descriptor unresolved at 0x{textureTable:X}");
        }

        if (!TryGetTextureView(colorTexture, out var viewFormat))
        {
            return RejectIndexedUi(DescribeIndexedUiTextureView("color", colorTexture, viewFormat));
        }

        if (!SupportsHostTextureAccess(viewFormat, requiredTextureSupport))
        {
            return RejectIndexedUi($"host color view format={viewFormat} does not support {requiredTextureSupport}");
        }

        if (!TryReadConstantDescriptor(table + IndexedUiVertexConstantsOffset(variant), minimumConstantBytes,
                out var vc))
        {
            return RejectIndexedUi($"vertex CBV unavailable: expectedBytes={minimumConstantBytes}");
        }

        var vertexConstantBytes = IndexedUiVertexConstantBytes(variant, vc.Size);
        var pixelConstantOffset = (vertexConstantBytes + IndexedUiConstantAlignment - 1) &
                                  ~(IndexedUiConstantAlignment - 1);
        var vertexOffset = pixelConstantOffset + IndexedUiConstantAlignment;
        var additionalUploadBytes = vertexOffset - RectangleVertexOffset;
        plan = plan with
        {
            IndexOffset = plan.IndexOffset + additionalUploadBytes,
            Capacity = plan.Capacity + additionalUploadBytes
        };
        if ((ulong)plan.Capacity > UploadBudget || state.UploadBytes > UploadBudget - (ulong)plan.Capacity)
        {
            return RejectIndexedUi("upload budget");
        }

        D3D12_SAMPLER_DESC sampler = default;
        ulong samplerHandle = 0;
        DescriptorToken token = default;

        if (usesSampler && !TryResolveUiSampler(state, out sampler, out samplerHandle, out token))
        {
            return RejectIndexedUi(
                $"selected sampler unresolved: handle=0x{samplerHandle:X}; tokenKind={token.Kind}; id={token.Id}");
        }

        var pipeline = GetIndexedUiPipeline(state, metadata, variant);
        if (pipeline == null)
        {
            return RejectIndexedUi("host PSO unavailable or output state unsupported");
        }

        if ((pipeline.ColorFormat == HostTextureViewFormats.A8Unorm && state.Depth.HasValue) ||
            !TryResolveClear(self, state.Targets[0], 20, out var targetHost, out var rtv, out var format,
                out var target,
                true) || format != pipeline.ColorFormat)
        {
            return RejectIndexedUi("bound targets incompatible with the host PSO");
        }

        state.RetainObject(colorTexture.Resource);
        if (MaterializeHostResource(colorTexture.Resource, out var textureHost) < 0 || textureHost == 0
            || textureHost == targetHost)
        {
            return RejectIndexedUi("native color source materialization or alias");
        }

        PlanarColorTextureBinding[] textures;
        if (additionalTexture != null)
        {
            state.RetainObject(additionalTexture.Resource);
            if (MaterializeHostResource(additionalTexture.Resource, out var additionalHost) < 0 ||
                additionalHost == 0 || additionalHost == targetHost || (dither && additionalHost == textureHost))
            {
                return RejectIndexedUi("native secondary texture materialization or alias");
            }

            textures = dither
                ?
                [
                    new PlanarColorTextureBinding(additionalTexture, additionalHost),
                    new PlanarColorTextureBinding(colorTexture, textureHost)
                ]
                :
                [
                    new PlanarColorTextureBinding(colorTexture, textureHost),
                    new PlanarColorTextureBinding(additionalTexture, additionalHost, secondaryDepthPlane)
                ];
        }
        else
        {
            textures = [new PlanarColorTextureBinding(colorTexture, textureHost)];
        }

        var heap = (nint)0;
        var samplers = (nint)0;
        var upload = (nint)0;
        try
        {
            if (!CreateIndexedUiHeap(0, (uint)textures.Length, out heap) ||
                (usesSampler && !CreateIndexedUiHeap(1, 1, out samplers)) ||
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

            D3D12_GPU_DESCRIPTOR_HANDLE samplerGpu = default;

            if (usesSampler)
            {
                sampler.MaxAnisotropy = Math.Clamp(sampler.MaxAnisotropy, 1U, 16U);
                _host->CreateSampler(&sampler, samplerHeap->GetCPUDescriptorHandleForHeapStart());
                samplerGpu = samplerHeap->GetGPUDescriptorHandleForHeapStart();
            }

            var gpu = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();
            var srv = srvHeap->GetGPUDescriptorHandleForHeapStart();

            if (gpu == 0 || (gpu & 255) != 0 || srv.ptr == 0 || (usesSampler && samplerGpu.ptr == 0))
            {
                return RejectIndexedUi("native GPU addresses");
            }

            var deferred = new IndexedUiUpload(plan, vertex.Address, table, textures, vc, pc, samplerHandle, token.Id,
                token.Kind,
                upload, target, ((ResourceObject*)target)->Generation, variant, format, vertexConstantBytes,
                pixelConstantOffset, vertexOffset);
            state.HostViews.Add(heap);

            if (usesSampler)
            {
                state.HostViews.Add(samplers);
            }

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
                BufferLocation = gpu + (uint)vertexOffset, SizeInBytes = (uint)plan.VertexBytes, StrideInBytes = stride
            };
            var ib = new D3D12_INDEX_BUFFER_VIEW
            {
                BufferLocation = gpu + (uint)plan.IndexOffset, SizeInBytes = (uint)plan.IndexBytes,
                Format = (DXGI_FORMAT)plan.IndexFormat
            };
            TransitionHostResource(list, targetHost, 0, 4);
            TransitionPlanarColorTextures(list, textures, 0, 0x80);
            commands->SetDescriptorHeaps(usesSampler ? 2U : 1U, (ID3D12DescriptorHeap**)heaps);
            commands->SetPipelineState((ID3D12PipelineState*)pipeline.Pipeline);
            commands->SetGraphicsRootSignature((ID3D12RootSignature*)pipeline.Root);
            commands->SetGraphicsRootConstantBufferView(0, gpu);
            commands->SetGraphicsRootConstantBufferView(1, gpu + (uint)pixelConstantOffset);
            commands->SetGraphicsRootDescriptorTable(2, srv);

            if (usesSampler)
            {
                commands->SetGraphicsRootDescriptorTable(3, samplerGpu);
            }

            commands->IASetVertexBuffers(0, 1, &vb);
            commands->IASetIndexBuffer(&ib);
            commands->OMSetRenderTargets(1, &nativeRtv, false, null);
            RecordHostGpuCommand(self, "indexed-ui DrawIndexedInstanced");
            commands->DrawIndexedInstanced(indices, 1, 0, baseVertex, 0);
            commands->OMSetRenderTargets(0, null, false, null);
            TransitionPlanarColorTextures(list, textures, 0x80, 0);
            TransitionHostResource(list, targetHost, 4, 0);
            SealMemoryBoundary(self, state, null, null, indexedUi: new IndexedUiBoundary(deferred, true));
            if (format == HostTextureViewFormats.A8Unorm)
            {
                TraceGraphicsSuccess(
                    $"[graphics] Indexed UI dithered A8 recorded: indices={indices}; samplerKind={token.Kind}.");
            }
            else if (dualAlpha)
            {
                TraceGraphicsSuccess(
                    $"[graphics] Indexed UI dual alpha mask recorded: indices={indices}; samplerKind={token.Kind}.");
            }
            else if (variant == IndexedUiVariant.BatchedMask)
            {
                TraceGraphicsSuccess(
                    $"[graphics] Indexed UI batched mask recorded: indices={indices}; vertexConstants={vertexConstantBytes}; samplerKind={token.Kind}.");
            }
            else if (variant == IndexedUiVariant.TransformedTexture)
            {
                TraceGraphicsSuccess(
                    $"[graphics] Indexed UI transformed texture recorded: indices={indices}; samplerKind={token.Kind}.");
            }
            else
            {
                TraceGraphicsSuccess(
                    $"[graphics] indexed-ui RECORDED: indices={indices}; stride={stride}; samplerKind={token.Kind}; dither={dither}; variant={variant}.");
            }

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

    private static string DescribeIndexedUiTextureView(string binding, TextureSource source, uint viewFormat)
    {
        var description = ((ResourceObject*)source.Resource)->Description;
        var viewBytes = source.Description == null ? "default" : Convert.ToHexString(source.Description);

        return $"{binding} SRV: descriptor={source.Token}; storage={description.Format}; view={viewFormat}; " +
               $"dimension={description.Dimension}; extent={description.Width}x{description.Height}x{description.DepthOrArraySize}; " +
               $"mips={description.MipLevels}; layout=0x{description.Layout:X}; flags=0x{description.Flags:X}; desc={viewBytes}";
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
            if (upload.ColorFormat == HostTextureViewFormats.A8Unorm)
            {
                TraceGraphicsSuccess("[graphics] Indexed UI dithered A8 GPU completed (visual content unverified).");
            }
            else if (upload.Variant == IndexedUiVariant.DualAlphaMask)
            {
                TraceGraphicsSuccess(
                    "[graphics] Indexed UI dual alpha mask GPU completed (visual content unverified).");
            }
            else if (upload.Variant == IndexedUiVariant.BatchedMask)
            {
                TraceGraphicsSuccess("[graphics] Indexed UI batched mask GPU completed (visual content unverified).");
            }
            else if (upload.Variant == IndexedUiVariant.BatchedDualTexture &&
                     upload.Textures is [_, { DepthPlaneSubresource: not null }])
            {
                TraceGraphicsSuccess("[graphics] Indexed UI batched dual texture with depth plane GPU completed.");
            }
            else if (upload.Variant == IndexedUiVariant.TransformedTexture)
            {
                TraceGraphicsSuccess(
                    "[graphics] Indexed UI transformed texture GPU completed (visual content unverified).");
            }
            else
            {
                TraceGraphicsSuccess(
                    $"[graphics] indexed-ui GPU COMPLETED: dither={upload.Dither}; variant={upload.Variant} (visual content unverified).");
            }

            TraceColor(queue, upload.Target, "indexed-ui-output");

            return;
        }

        var pixelConstantBytes = IndexedUiCompiler.PixelConstantBytes(upload.Variant);

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0 || !IsResource(upload.Target) ||
            ((ResourceObject*)upload.Target)->Generation != upload.Generation
            || !upload.Textures.All(static t => IsTextureSourceCurrent(t.Source)) ||
            (IndexedUiUsesSampler(upload.Variant) &&
                (!TryReadMappingRecord(upload.SamplerHandle, out DescriptorToken sampler) ||
                 sampler.Tag != DescriptorTag || sampler.Id != upload.SamplerToken || sampler.Kind != upload.SamplerKind)) ||
            !TryReadConstantDescriptor(upload.Table + IndexedUiVertexConstantsOffset(upload.Variant),
                upload.VertexConstantBytes, out var vc) || !MatchesConstantDescriptor(vc, upload.VertexConstants) ||
            (pixelConstantBytes != 0 &&
                (!TryReadConstantDescriptor(upload.Table + IndexedUiPixelConstantTableOffset, pixelConstantBytes, out var pc) ||
                 !MatchesConstantDescriptor(pc, upload.PixelConstants))))
        {
            Environment.FailFast("[graphics] indexed-ui submission source changed or invalid.");
        }

        StageIndexedUiUpload(upload);
        TraceIndexedUiSources(queue, upload);
        PreparePlanarColorTextures(queue, upload.Textures, false, ordered);
        TraceGraphicsSuccess("[graphics] indexed-ui PREPARED.");
    }
}
