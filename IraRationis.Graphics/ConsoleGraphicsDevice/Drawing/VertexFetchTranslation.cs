// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Drawing;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Shaders;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Shaders.Compute;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Raster;
using IraRationis.Shaders.VertexFetch;
using IraRationis.Shaders.VertexFetch.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly HostShaderBytecodeCache TranslatedShaderCache = new(256, 64L << 20);
    private static long _translatedShaderCacheHits;

    // This is an executable host-bytecode preparation stage, not draw execution.
    // A successful compiler or root-signature result does not establish PSO,
    // resource binding, memory coherence or guest output correctness.
    private static void PrepareVertexFetchTranslation(TextWriter report, RecordedCommandState state,
        GraphicsPipelineMetadata pipeline, string? directory, VertexFetchDrawRange? draw)
    {
        if (pipeline.Shaders.Count != 2 || pipeline.Root != state.Graphics.Signature ||
            !IsRootSignature(pipeline.Root))
        {
            report.WriteLine(
                "[graphics] R10 translation refused: expected a bound matching two-stage graphics pipeline.");

            return;
        }

        var vertex =
            pipeline.Shaders.SingleOrDefault(static shader => shader.Stage == 1);

        var pixel =
            pipeline.Shaders.SingleOrDefault(static shader => shader.Stage == 2);

        if (vertex == null || pixel == null)
        {
            report.WriteLine("[graphics] R10 translation refused: missing vertex/pixel stage pair.");

            return;
        }

        try
        {
            var root = RootSignatureBlob(pipeline.Root).ToArray();

            var pair =
                Rdna2VertexFetchCompiler.Translate(vertex.Container.Bytes, pixel.Container.Bytes, root);

            report.WriteLine(
                $"[graphics] IRX-GFX-SHADER-R10: recognized native vertex-fetch / P0 half-RTZ shader pair; " +
                $"SRV t{pair.Register}, space{pair.Space}; position header=0x{pair.Position.HeaderOffset:X}, " +
                $"attribute header=0x{pair.Attribute.HeaderOffset:X}.");

            if (directory != null)
            {
                File.WriteAllText(
                    Path.Combine(directory, "translated-vs.hlsl"), pair.VertexSource);

                File.WriteAllText(
                    Path.Combine(directory, "translated-ps.hlsl"), pair.PixelSource);
            }

            // The Windows compiler is a system-provided native API invoked
            // directly by C# AOT. It does not add a C++ IRX project or bridge.
            var vertexShader = CompileHostShader(pair.VertexSource, "vs_5_1", report);
            var pixelShader = CompileHostShader(pair.PixelSource, "ps_5_1", report);

            if (directory != null)
            {
                if (vertexShader != null)
                {
                    File.WriteAllBytes(
                        Path.Combine(directory, "translated-vs.dxbc"), vertexShader);
                }

                if (pixelShader != null)
                {
                    File.WriteAllBytes(
                        Path.Combine(directory, "translated-ps.dxbc"), pixelShader);
                }
            }

            ValidateHostPipeline(pair, vertexShader, pixelShader, pipeline, state.Depth.HasValue, report);

            if (draw is { } range)
            {
                InspectGuestSource(pair, state, range, report);
            }

            if (vertexShader != null && pixelShader != null)
            {
                report.WriteLine(
                    "[graphics] R10 host shader compilation passed for both stages (not bound or executed).");
            }

            report.WriteLine($"[graphics] R10 draw remains blocked: PSO stream complete={pipeline.CompleteStream}; " +
                             "persistent native PSO ownership, descriptor binding, resource coherence and execution remain outstanding.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or UnauthorizedAccessException or
                                              ArgumentException or NotSupportedException or OverflowException or
                                              DllNotFoundException or EntryPointNotFoundException
                                              or OutOfMemoryException)
        {
            report.WriteLine($"[graphics] R10 translation refused: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static byte[]? CompileHostShader(string source, string profile, TextWriter report)
    {
        var bytecode = TranslatedShaderCache.GetOrCompile(source, profile, Compile, out var reused);

        if (!reused || bytecode == null)
        {
            return bytecode;
        }

        var hits = Interlocked.Increment(ref _translatedShaderCacheHits);

        if (hits <= 4 || (hits & (hits - 1)) == 0)
        {
            report.WriteLine(
                $"[graphics] IRX-GFX-SHADER-R19: cache hit #{hits}, {profile}, bytes={bytecode.Length}.");
        }

        return bytecode;

        byte[]? Compile()
        {
            BeginShaderCompilation(profile);

            var succeeded = false;

            try
            {
                var result = CompileHostShaderCore(source, profile, report);

                succeeded = result != null;

                return result;
            }
            finally
            {
                EndShaderCompilation(succeeded);
            }
        }
    }

    private static byte[]? CompileHostShaderCore(string source, string profile, TextWriter report)
    {
        return CompilePersistentShader(source, profile, report);
    }

    private static byte[]? CompileShaderUncached(string source, string profile, TextWriter report)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var target = Encoding.ASCII.GetBytes(profile + "\0");
        var code = (nint)0;
        var errors = (nint)0;

        try
        {
            fixed (byte* text = sourceBytes)
            fixed (byte* entry = "main\0"u8)
            fixed (byte* targetProfile = target)
            {
                // ENABLE_STRICTNESS | OPTIMIZATION_LEVEL3. No skip validation.
                var resultCode = CompileHostShaderNative(text, (nuint)sourceBytes.Length, null, null, null,
                    entry, targetProfile, 0x8800, 0, &code, &errors);

                if (errors != 0)
                {
                    var message = ReadShaderBlob(errors, 1024 * 1024);

                    report.WriteLine(
                        $"[graphics] R10 {profile} compiler: {Encoding.UTF8.GetString(message).TrimEnd('\0')}");
                }

                report.WriteLine($"[graphics] R10 {profile} compilation HRESULT=0x{resultCode:X8}.");

                if (resultCode < 0 || code == 0)
                {
                    return null;
                }
            }

            var bytes = ReadShaderBlob(code, 16 * 1024 * 1024);
            var container = ShaderContainer.Parse(bytes);

            if (container.Part("SHEX") == null && container.Part("SHDR") == null)
            {
                throw new InvalidDataException("Host compiler returned no executable shader bytecode.");
            }

            report.WriteLine($"[graphics] R10 {profile} host DXBC bytes={bytes.Length}.");

            return bytes;
        }
        finally
        {
            if (errors != 0)
            {
                _ = HostRelease(errors);
            }

            if (code != 0)
            {
                _ = HostRelease(code);
            }
        }
    }

    private static byte[] ReadShaderBlob(nint blob, int limit)
    {
        // ID3DBlob: IUnknown (0..2), GetBufferPointer (3), GetBufferSize (4).
        var pointer = ((delegate* unmanaged[Stdcall]<nint, void*>)Slot(blob, 3))(blob);
        var size = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(blob, 4))(blob);

        if (pointer == null || size == 0 || size > (nuint)limit)
        {
            throw new InvalidDataException("Invalid host compiler blob size.");
        }

        return new ReadOnlySpan<byte>(pointer, checked((int)size)).ToArray();
    }

    private static bool TryPrepareVertexFetchHostState(GraphicsPipelineMetadata guest, bool depthBound,
        out GraphicsPipelineMetadata host, out uint colorFormat)
    {
        host = guest;
        colorFormat = 0;

        if (!guest.IsStream || !guest.CompleteStream || guest.InputLayout is { Length: > 0 })
        {
            return false;
        }

        var targets =
            guest.State.SingleOrDefault(static record => record.Kind == 15);

        var depthFormat =
            guest.State.SingleOrDefault(static record => record.Kind == 16);

        if (targets is not { Bytes.Length: 40 } || depthFormat is not { Bytes.Length: 8 } ||
            BinaryPrimitives.ReadUInt32LittleEndian(targets.Bytes) != 15 ||
            BinaryPrimitives.ReadUInt32LittleEndian(targets.Bytes.AsSpan(36)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(depthFormat.Bytes) != 16)
        {
            return false;
        }

        colorFormat = BinaryPrimitives.ReadUInt32LittleEndian(targets.Bytes.AsSpan(4));

        if (depthBound)
        {
            return colorFormat == HostTextureViewFormats.Rgba8Unorm &&
                   BinaryPrimitives.ReadUInt32LittleEndian(depthFormat.Bytes.AsSpan(4)) == 20;
        }

        return colorFormat is HostTextureViewFormats.Rgba8Unorm or HostTextureViewFormats.A8Unorm &&
               DepthTargetPolicy.TryGetHostState(guest, false, out host);
    }

    private static void ValidateHostPipeline(VertexFetchShaderPair pair, byte[]? vertexShader, byte[]? pixelShader,
        GraphicsPipelineMetadata pipeline, bool depthBound, TextWriter report)
    {
        if (_host == null)
        {
            report.WriteLine("[graphics] R10 host root validation unavailable: no host device.");

            return;
        }

        if (!TryPrepareVertexFetchHostState(pipeline, depthBound, out var hostState, out var colorFormat))
        {
            report.WriteLine("[graphics] Vertex-fetch validation refused the captured output-merger state.");

            return;
        }

        var root = SerializeHostRoot(pair, report);

        if (root == null)
        {
            return;
        }

        var iid = RootSignatureId;
        var native = (void*)null;

        try
        {
            fixed (byte* blob = root)
            {
                var resultCode = (int)_host->CreateRootSignature(0, blob, (nuint)root.Length, &iid, &native);

                report.WriteLine($"[graphics] R10 host CreateRootSignature HRESULT=0x{resultCode:X8}; " +
                                 $"created={native != null} (validation only).");
            }

            if (native == null || vertexShader == null || pixelShader == null)
            {
                return;
            }

            var candidate = CreatePipelineCandidate(native, vertexShader, pixelShader, hostState, report,
                allowDepthless: !depthBound, colorFormat: colorFormat);

            if (candidate == 0)
            {
                return;
            }

            try
            {
                VerifyVertexFetchTranslatedDraw(pair, (nint)native, candidate, colorFormat, depthBound, report);
            }
            finally
            {
                _ = HostRelease(candidate);
            }
        }
        finally
        {
            if (native != null)
            {
                _ = HostRelease((nint)native);
            }
        }
    }

    private static byte[]? SerializeHostRoot(VertexFetchShaderPair pair, TextWriter report, bool rectangle = false,
        bool dualMask = false)
    {
        // Rebuild the validated version 1.1 root contract with host structures.
        // Do not rely on the console container checksum or copy guest pointers.
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[2];

        parameters[0] = new D3D12_ROOT_PARAMETER1
        {
            ParameterType = (D3D12_ROOT_PARAMETER_TYPE)(rectangle ? 2 : 3),
            ShaderVisibility = 0
        };

        parameters[0].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1
        {
            ShaderRegister = pair.Register,
            RegisterSpace = pair.Space,
            Flags = 0
        };

        var maskRange = new D3D12_DESCRIPTOR_RANGE1
        {
            RangeType = D3D12_DESCRIPTOR_RANGE_TYPE.D3D12_DESCRIPTOR_RANGE_TYPE_SRV,
            NumDescriptors = 2
        };

        var maskSampler = new D3D12_STATIC_SAMPLER_DESC
        {
            Filter = D3D12_FILTER.D3D12_FILTER_MIN_MAG_MIP_POINT,
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_WRAP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_WRAP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_WRAP,
            MaxAnisotropy = 1,
            ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER,
            ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL
        };

        if (dualMask)
        {
            parameters[1] = new D3D12_ROOT_PARAMETER1
            {
                ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE,
                ShaderVisibility = D3D12_SHADER_VISIBILITY.D3D12_SHADER_VISIBILITY_PIXEL
            };

            parameters[1].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
            {
                NumDescriptorRanges = 1,
                pDescriptorRanges = &maskRange
            };
        }

        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC
        {
            Version = (D3D_ROOT_SIGNATURE_VERSION)2
        };

        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = dualMask ? 2U : 1U,
            pParameters = parameters,
            NumStaticSamplers = dualMask ? 1U : 0U,
            pStaticSamplers = dualMask ? &maskSampler : null,
            Flags = (D3D12_ROOT_SIGNATURE_FLAGS)(rectangle ? 1 : 0)
        };

        var blob = (nint)0;
        var errors = (nint)0;

        try
        {
            var resultCode =
                SerializeRootNative(&description, &blob, &errors);

            report.WriteLine($"[graphics] R10 host root serialization HRESULT=0x{resultCode:X8}.");

            if (errors != 0)
            {
                report.WriteLine(Encoding.UTF8.GetString(ReadShaderBlob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            return resultCode >= 0 && blob != 0
                ? ReadShaderBlob(blob, 1024 * 1024)
                : null;
        }
        finally
        {
            if (errors != 0)
            {
                _ = HostRelease(errors);
            }

            if (blob != 0)
            {
                _ = HostRelease(blob);
            }
        }
    }

    // A Windows pipeline stream candidate is constructed only when all required
    // guest state was captured. No guest pointer enters this host stream. This
    // returns an owned host PSO; the caller must release or transfer that reference.
    private static nint CreatePipelineCandidate(void* root, byte[] vertexShader, byte[] pixelShader,
        GraphicsPipelineMetadata pipeline, TextWriter report, byte[]? geometryShader = null, void* inputElements = null,
        uint inputCount = 0, bool allowDepthless = false, uint colorFormat = HostTextureViewFormats.Rgba8Unorm)
    {
        var rectangle = geometryShader != null;
        var hasInput = inputElements != null && inputCount != 0;

        if (allowDepthless && !HasDisabledDepthStencil(pipeline))
        {
            report.WriteLine("[graphics] Depthless pipeline requires disabled depth, stencil, and depth bounds.");

            return 0;
        }

        if (inputElements == null != (inputCount == 0) || (rectangle && !hasInput) ||
            (!hasInput && pipeline.InputLayout is { Length: > 0 }) ||
            (colorFormat == HostTextureViewFormats.A8Unorm && (rectangle || !allowDepthless)))
        {
            report.WriteLine("[graphics] Host PSO input layout incompatible with selected shader path.");

            return 0;
        }

        if (!pipeline.IsStream || !pipeline.CompleteStream || pipeline.State.Count != 8 ||
            sizeof(D3D12_BLEND_DESC) != 328 || sizeof(D3D12_RASTERIZER_DESC) != 44 ||
            sizeof(D3D12_DEPTH_STENCIL_DESC1) != 56 || sizeof(D3D12_RT_FORMAT_ARRAY) != 36)
        {
            report.WriteLine("[graphics] R10 native PSO validation refused: incomplete or unsupported stream layout.");

            return 0;
        }

        var seen = 0U;
        var fixedSize = 64 + (rectangle ? 24 : 0) + (hasInput ? 24 : 0);
        var size = fixedSize;

        foreach (var record in pipeline.State)
        {
            var payload = ShaderPipelinePayloadSize(record.Kind);
            var expected = payload == 0 ? 0 : (payload + 4 + 7) & ~7;

            if (expected == 0 || record.Bytes.Length != expected ||
                BinaryPrimitives.ReadUInt32LittleEndian(record.Bytes) != record.Kind ||
                (seen & (1U << (int)record.Kind)) != 0 ||
                !IsPipelineStateCompatible(record, rectangle, allowDepthless, colorFormat))
            {
                report.WriteLine($"[graphics] R10 native PSO validation refused: unsupported state {record.Kind}.");

                return 0;
            }

            seen |= 1U << (int)record.Kind;

            size += expected;
        }

        // Exact eight state records, in any order. D3D12 validates the contents
        // of the standard-layout fixed-function structures at creation time.
        const uint required = (1U << 8) | (1U << 9) | (1U << 10) | (1U << 14) |
                              (1U << 15) | (1U << 16) | (1U << 17) | (1U << 21);

        if (seen != required)
        {
            report.WriteLine("[graphics] R10 native PSO validation refused: required state missing.");

            return 0;
        }

        var stream = new byte[size];
        var offset = fixedSize;

        foreach (var record in pipeline.State)
        {
            var payload = ShaderPipelinePayloadSize(record.Kind);
            // Copy the tag and standard payload, not guest padding or pointers.
            record.Bytes.AsSpan(0, 4 + payload).CopyTo(stream.AsSpan(offset));

            if (rectangle && record.Kind == 14)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(offset + 4), 3);
            }

            offset += record.Bytes.Length;
        }

        var device2Id = new Guid("30baa41e-b15b-475c-a0bb-1af5c5b64328");
        var device2 = (void*)null;
        var nativePipeline = (void*)null;

        try
        {
            var resultCode = (int)_host->QueryInterface(&device2Id, &device2);

            if (resultCode < 0 || device2 == null)
            {
                report.WriteLine($"[graphics] R10 ID3D12Device2 query HRESULT=0x{resultCode:X8}.");

                return 0;
            }

            fixed (byte* bytes = stream)
            fixed (byte* vertex = vertexShader)
            fixed (byte* pixel = pixelShader)
            fixed (byte* geometry = geometryShader)
            {
                if (((nuint)bytes & 7U) != 0)
                {
                    report.WriteLine("[graphics] R10 native PSO validation refused: stream is not pointer aligned.");

                    return 0;
                }

                Unsafe.WriteUnaligned(bytes + 8, (nint)root);

                Unsafe.WriteUnaligned(bytes + 16, 1U); // VS
                Unsafe.WriteUnaligned(bytes + 24, (nint)vertex);

                Unsafe.WriteUnaligned(bytes + 32, (nuint)vertexShader.Length);

                Unsafe.WriteUnaligned(bytes + 40, 2U); // PS
                Unsafe.WriteUnaligned(bytes + 48, (nint)pixel);

                Unsafe.WriteUnaligned(bytes + 56, (nuint)pixelShader.Length);

                if (rectangle)
                {
                    Unsafe.WriteUnaligned(bytes + 64, 5U); // GS
                    Unsafe.WriteUnaligned(bytes + 72, (nint)geometry);

                    Unsafe.WriteUnaligned(bytes + 80, (nuint)geometryShader!.Length);
                }

                if (hasInput)
                {
                    var inputOffset = rectangle ? 88 : 64;

                    Unsafe.WriteUnaligned(bytes + inputOffset, 12U); // INPUT_LAYOUT
                    Unsafe.WriteUnaligned(bytes + inputOffset + 8, (nint)inputElements);

                    Unsafe.WriteUnaligned(bytes + inputOffset + 16, inputCount);
                }

                var description = new D3D12_PIPELINE_STATE_STREAM_DESC
                {
                    SizeInBytes = (nuint)stream.Length,
                    pPipelineStateSubobjectStream = bytes
                };
                var iid = PipelineId;

                resultCode = ((ID3D12Device2*)device2)->CreatePipelineState(&description, &iid, &nativePipeline);

                report.WriteLine($"[graphics] R10 native CreatePipelineState HRESULT=0x{resultCode:X8}; " +
                                 $"created={nativePipeline != null}.");

                if (resultCode < 0 || nativePipeline == null)
                {
                    return 0;
                }

                var result = (nint)nativePipeline;

                nativePipeline = null;

                return result;
            }
        }
        finally
        {
            if (nativePipeline != null)
            {
                _ = HostRelease((nint)nativePipeline);
            }

            if (device2 != null)
            {
                _ = HostRelease((nint)device2);
            }
        }
    }

    private static int ShaderPipelinePayloadSize(uint kind)
    {
        return kind switch
        {
            8 => sizeof(D3D12_BLEND_DESC),
            9 or 14 or 16 => sizeof(uint),
            10 => sizeof(D3D12_RASTERIZER_DESC),
            15 => sizeof(D3D12_RT_FORMAT_ARRAY),
            17 => 8,
            21 => sizeof(D3D12_DEPTH_STENCIL_DESC1),
            _ => 0
        };
    }

    private static bool HasDisabledDepthStencil(GraphicsPipelineMetadata pipeline)
    {
        var depth = pipeline.State.SingleOrDefault(static record => record.Kind == 21);
        var format = pipeline.State.SingleOrDefault(static record => record.Kind == 16);

        return depth is { Bytes.Length: 64 } && format is { Bytes.Length: 8 } &&
               BinaryPrimitives.ReadUInt32LittleEndian(format.Bytes.AsSpan(4)) == 0 &&
               BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(4)) == 0 &&
               BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(16)) == 0 &&
               BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(56)) == 0;
    }

    private static bool IsPipelineStateCompatible(PipelineStateRecord record, bool rectangle,
        bool allowDepthless, uint colorFormat)
    {
        var bytes = record.Bytes.AsSpan();

        // Validate the format selected for this shader candidate. D3D12 validates
        // the remaining standard fixed-function state when it creates the PSO.
        return record.Kind switch
        {
            14 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == (rectangle ? 19U : 3U),
            15 => colorFormat is HostTextureViewFormats.Rgba8Unorm or HostTextureViewFormats.A8Unorm &&
                  BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == colorFormat &&
                  (colorFormat != HostTextureViewFormats.A8Unorm || !bytes[8..36].ContainsAnyExcept((byte)0)) &&
                  BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]) == 1,
            16 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 20 ||
                  ((rectangle || allowDepthless) && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 0),
            17 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 1 &&
                  BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == 0,
            _ => true
        };
    }

    private static void VerifyVertexFetchExtent()
    {
        HostShaderBytecodeCache.Verify();

        var pair = new VertexFetchShaderPair(string.Empty, string.Empty, 3, 0, new VertexFetchField(0, 3),
            new VertexFetchField(12, 4));

        Span<byte> header = stackalloc byte[16];

        header.Clear();

        BinaryPrimitives.WriteUInt32LittleEndian(header, (64U << 7) | (12U << 3) | 3U);

        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (128U << 7) | (16U << 3) | 4U);

        if (!Rdna2VertexFetchCompiler.TryGetReadExtent(pair, header, 0, 3, 1024, out var bytes) || bytes != 176 ||
            Rdna2VertexFetchCompiler.TryGetReadExtent(pair, header, 0, 3, 175, out _) ||
            Rdna2VertexFetchCompiler.TryGetReadExtent(pair, header, uint.MaxValue, 2, ulong.MaxValue, out _) ||
            Rdna2VertexFetchCompiler.TryGetReadExtent(pair, header[..12], 0, 3, 1024, out _))
        {
            throw new InvalidOperationException("R10 vertex fetch range validation failed.");
        }

        var synthetic = Rdna2VertexFetchCompiler.VerifySyntheticRecognition();

        Console.WriteLine("[graphics-probe] R10 vertex fetch recognition and source-range planning passed (CPU only).");

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var vertex = CompileHostShader(synthetic.VertexSource, "vs_5_1", Console.Out);
        var pixel = CompileHostShader(synthetic.PixelSource, "ps_5_1", Console.Out);
        var root = SerializeHostRoot(synthetic, Console.Out);

        if (vertex == null || pixel == null || root == null)
        {
            throw new InvalidOperationException("R10 synthetic host shader/root serialization failed.");
        }

        Console.WriteLine(
            "[graphics-probe] R10 synthetic host shader compilation and root serialization passed (no GPU draw).");

        var strip = SrgbTextureStripCompiler.VerifyAuthoredContracts();
        var stripVertex = CompileHostShader(strip.VertexSource, "vs_5_1", Console.Out);
        var stripPixel = CompileHostShader(strip.PixelSource, "ps_5_1", Console.Out);
        var stripRoot = SerializePlanarColorRoot(Console.Out);

        if (stripVertex == null || stripPixel == null || stripRoot == null)
        {
            throw new InvalidOperationException("sRGB texture-strip host shader/root serialization failed.");
        }

        Console.WriteLine(
            "[graphics-probe] sRGB texture-strip host shader compilation and root serialization passed (no GPU draw).");

        foreach (var format in new uint[] { 41, 56, 58, 16, 35, 37, 2, 28, 31 }
                     .Where(static format =>
                         CompileHostShader(CurveLutCompiler
                             .CreateHostSource(format), "cs_5_1", Console.Out) == null))
        {
            throw new InvalidOperationException($"Curve LUT host shader compilation failed for format {format}.");
        }

        if (SerializeColorLutRoot() == null)
        {
            throw new InvalidOperationException("Curve LUT shared compute root serialization failed.");
        }

        if (CompileHostShader(CurveLutCompiler.CreateHostSource(41, 3), "cs_5_1", Console.Out) == null)
        {
            throw new InvalidOperationException("Three-segment curve LUT host shader compilation failed.");
        }

        Console.WriteLine(
            "[graphics-probe] Curve LUT host shader variants and root serialization passed (no GPU dispatch).");

        VerifyIndexedUiSampler();
        VerifyIndexedUiDualAlpha();
        VerifyIndexedUiBatchedMask();
        VerifyIndexedUiBatchedDualTexture();
        VerifyIndexedUiTransformedTexture();

        //TODO: This is a crazy conditional we need to review...
        if (CompileHostShader(IndexedUiCompiler.VertexSource, "vs_5_1", Console.Out) == null ||
            CompileHostShader(IndexedUiCompiler.PixelSource, "ps_5_1", Console.Out) == null
            || SerializeIndexedUiRoot() == null ||
            CompileHostShader(IndexedUiCompiler.PlainPixelSource, "ps_5_1", Console.Out) == null
            || SerializeIndexedUiRoot(false) == null ||
            !IndexedUiCompiler.TryClassify(IndexedUiCompiler.VertexHash, IndexedUiCompiler.PixelHash, out var dither)
            || dither != IndexedUiVariant.Dithered ||
            !IndexedUiCompiler.TryClassify(IndexedUiCompiler.PlainVertexHash, IndexedUiCompiler.PlainPixelHash,
                out var plainVariant) || plainVariant != IndexedUiVariant.Plain ||
            CompileHostShader(IndexedUiCompiler.AlphaVertexSource, "vs_5_1", Console.Out) == null ||
            CompileHostShader(IndexedUiCompiler.AlphaPixelSource, "ps_5_1", Console.Out) == null ||
            !IndexedUiCompiler.TryClassify(IndexedUiCompiler.AlphaVertexHash, IndexedUiCompiler.AlphaPixelHash,
                out var alphaVariant) || alphaVariant != IndexedUiVariant.AlphaMask ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.VertexHash, IndexedUiCompiler.PlainPixelHash, out _) ||
            IndexedUiCompiler.TryClassify(IndexedUiCompiler.AlphaVertexHash, IndexedUiCompiler.PlainPixelHash, out _))
        {
            throw new InvalidOperationException("Indexed UI host shader/root serialization failed.");
        }

        Console.WriteLine(
            "[graphics-probe] Indexed UI host shader compilation and root serialization passed (no GPU draw).");
    }

    [LibraryImport("d3d12.dll", EntryPoint = "D3D12SerializeVersionedRootSignature")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SerializeRootNative(D3D12_VERSIONED_ROOT_SIGNATURE_DESC* description,
        nint* blob, nint* errors);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CompileHostShaderNative(void* source, nuint size, byte* name, void* defines,
        void* include,
        byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);
}
