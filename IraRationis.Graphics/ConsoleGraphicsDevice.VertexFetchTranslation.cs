// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using IraRationis.Shaders;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    // This is an executable host-bytecode preparation stage, not draw execution.
    // A successful compiler or root-signature result does not establish PSO,
    // resource binding, memory coherence or guest output correctness.
    private static void PrepareVertexFetchTranslation(TextWriter report, RecordedCommandState state,
        GraphicsPipelineMetadata pipeline, string? directory)
    {
        if (pipeline.Shaders.Count != 2 || pipeline.Root != state.Graphics.Signature ||
            !IsRootSignature(pipeline.Root))
        {
            report.WriteLine(
                "[graphics] R10 translation refused: expected a bound matching two-stage graphics pipeline.");

            return;
        }

        var vertex = pipeline.Shaders.SingleOrDefault(static shader => shader.Stage == 1);
        var pixel = pipeline.Shaders.SingleOrDefault(static shader => shader.Stage == 2);
        if (vertex == null || pixel == null)
        {
            report.WriteLine("[graphics] R10 translation refused: missing vertex/pixel stage pair.");

            return;
        }

        try
        {
            var root = RootSignatureBlob(pipeline.Root).ToArray();
            var pair = Rdna2VertexFetchCompiler.Translate(vertex.Container.Bytes, pixel.Container.Bytes, root);
            report.WriteLine(
                $"[graphics] IRX-GFX-SHADER-R10: recognized native vertex-fetch / P0 half-RTZ shader pair; " +
                $"SRV t{pair.Register}, space{pair.Space}; position header=0x{pair.Position.HeaderOffset:X}, " +
                $"attribute header=0x{pair.Attribute.HeaderOffset:X}.");
            if (directory != null)
            {
                File.WriteAllText(Path.Combine(directory, "translated-vs.hlsl"), pair.VertexSource);
                File.WriteAllText(Path.Combine(directory, "translated-ps.hlsl"), pair.PixelSource);
            }

            // The Windows compiler is a system-provided native API invoked
            // directly by C# AOT. It does not add a C++ IRX project or bridge.
            var vs = CompileR10Shader(pair.VertexSource, "vs_5_1", report);
            var ps = CompileR10Shader(pair.PixelSource, "ps_5_1", report);
            if (directory != null)
            {
                if (vs != null)
                {
                    File.WriteAllBytes(Path.Combine(directory, "translated-vs.dxbc"), vs);
                }

                if (ps != null)
                {
                    File.WriteAllBytes(Path.Combine(directory, "translated-ps.dxbc"), ps);
                }
            }

            ValidateR10HostPipeline(pair, vs, ps, pipeline, report);
            if (vs != null && ps != null)
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

    private static byte[]? CompileR10Shader(string source, string profile, TextWriter report)
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
                var hr = CompileR10ShaderNative(text, (nuint)sourceBytes.Length, null, null, null,
                    entry, targetProfile, 0x8800, 0, &code, &errors);
                if (errors != 0)
                {
                    var message = ReadR10Blob(errors, 1024 * 1024);
                    report.WriteLine(
                        $"[graphics] R10 {profile} compiler: {Encoding.UTF8.GetString(message).TrimEnd('\0')}");
                }

                report.WriteLine($"[graphics] R10 {profile} compilation HRESULT=0x{hr:X8}.");
                if (hr < 0 || code == 0)
                {
                    return null;
                }
            }

            var bytes = ReadR10Blob(code, 16 * 1024 * 1024);
            var container = Shaders.ShaderContainer.Parse(bytes);
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

    private static byte[] ReadR10Blob(nint blob, int limit)
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

    private static void ValidateR10HostPipeline(VertexFetchShaderPair pair, byte[]? vs, byte[]? ps,
        GraphicsPipelineMetadata pipeline, TextWriter report)
    {
        if (_host == null)
        {
            report.WriteLine("[graphics] R10 host root validation unavailable: no host device.");

            return;
        }

        var root = SerializeR10HostRoot(pair, report);
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
                var hr = (int)_host->CreateRootSignature(0, blob, (nuint)root.Length, &iid, &native);
                report.WriteLine($"[graphics] R10 host CreateRootSignature HRESULT=0x{hr:X8}; " +
                                 $"created={native != null} (validation only).");
            }

            if (native != null && vs != null && ps != null)
            {
                ValidateR10PipelineCandidate(native, vs, ps, pipeline, report);
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

    private static byte[]? SerializeR10HostRoot(VertexFetchShaderPair pair, TextWriter report)
    {
        // Rebuild the validated version 1.1 root contract with host structures.
        // Do not rely on the console container checksum or copy guest pointers.
        var parameter = new D3D12_ROOT_PARAMETER1
        {
            ParameterType = (D3D12_ROOT_PARAMETER_TYPE)3,
            ShaderVisibility = 0
        };
        parameter.Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1
        {
            ShaderRegister = pair.Register,
            RegisterSpace = pair.Space,
            Flags = 0
        };
        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC
        {
            Version = (D3D_ROOT_SIGNATURE_VERSION)2
        };
        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = 1,
            pParameters = &parameter,
            Flags = 0
        };
        var blob = (nint)0;
        var errors = (nint)0;
        try
        {
            var hr = SerializeR10RootNative(&description, &blob, &errors);
            report.WriteLine($"[graphics] R10 host root serialization HRESULT=0x{hr:X8}.");
            if (errors != 0)
            {
                report.WriteLine(Encoding.UTF8.GetString(ReadR10Blob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            return hr >= 0 && blob != 0 ? ReadR10Blob(blob, 1024 * 1024) : null;
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
    // creates and immediately releases a host PSO, never submits a draw.
    private static void ValidateR10PipelineCandidate(void* root, byte[] vs, byte[] ps,
        GraphicsPipelineMetadata pipeline, TextWriter report)
    {
        if (!pipeline.IsStream || !pipeline.CompleteStream || pipeline.State.Count != 8 ||
            sizeof(D3D12_BLEND_DESC) != 328 || sizeof(D3D12_RASTERIZER_DESC) != 44 ||
            sizeof(D3D12_DEPTH_STENCIL_DESC1) != 56 || sizeof(D3D12_RT_FORMAT_ARRAY) != 36)
        {
            report.WriteLine("[graphics] R10 native PSO validation refused: incomplete or unsupported stream layout.");

            return;
        }

        var seen = 0U;
        var size = 64; // root (16), VS (24), PS (24)
        foreach (var record in pipeline.State)
        {
            var payload = R10PipelinePayloadSize(record.Kind);
            var expected = payload == 0 ? 0 : (payload + 4 + 7) & ~7;
            if (expected == 0 || record.Bytes.Length != expected ||
                BinaryPrimitives.ReadUInt32LittleEndian(record.Bytes) != record.Kind ||
                (seen & (1U << (int)record.Kind)) != 0 || !R10PipelineStateCompatible(record))
            {
                report.WriteLine($"[graphics] R10 native PSO validation refused: unsupported state {record.Kind}.");

                return;
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

            return;
        }

        var stream = new byte[size];
        var offset = 64;
        foreach (var record in pipeline.State)
        {
            var payload = R10PipelinePayloadSize(record.Kind);
            // Copy the tag and standard payload, not guest padding or pointers.
            record.Bytes.AsSpan(0, 4 + payload).CopyTo(stream.AsSpan(offset));
            offset += record.Bytes.Length;
        }

        var device2Id = new Guid("30baa41e-b15b-475c-a0bb-1af5c5b64328");
        var device2 = (void*)null;
        var nativePipeline = (void*)null;
        try
        {
            var hr = (int)_host->QueryInterface(&device2Id, &device2);
            if (hr < 0 || device2 == null)
            {
                report.WriteLine($"[graphics] R10 ID3D12Device2 query HRESULT=0x{hr:X8}.");

                return;
            }

            fixed (byte* bytes = stream)
            fixed (byte* vertex = vs)
            fixed (byte* pixel = ps)
            {
                if (((nuint)bytes & 7U) != 0)
                {
                    report.WriteLine("[graphics] R10 native PSO validation refused: stream is not pointer aligned.");

                    return;
                }

                Unsafe.WriteUnaligned(bytes + 8, (nint)root);
                Unsafe.WriteUnaligned(bytes + 16, 1U); // VS
                Unsafe.WriteUnaligned(bytes + 24, (nint)vertex);
                Unsafe.WriteUnaligned(bytes + 32, (nuint)vs.Length);
                Unsafe.WriteUnaligned(bytes + 40, 2U); // PS
                Unsafe.WriteUnaligned(bytes + 48, (nint)pixel);
                Unsafe.WriteUnaligned(bytes + 56, (nuint)ps.Length);
                var description = new D3D12_PIPELINE_STATE_STREAM_DESC
                {
                    SizeInBytes = (nuint)stream.Length,
                    pPipelineStateSubobjectStream = bytes
                };
                var iid = PipelineId;
                hr = ((ID3D12Device2*)device2)->CreatePipelineState(&description, &iid, &nativePipeline);
                report.WriteLine($"[graphics] R10 native CreatePipelineState HRESULT=0x{hr:X8}; " +
                                 $"created={nativePipeline != null} (validation only, not bound).");
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

    private static int R10PipelinePayloadSize(uint kind)
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

    private static bool R10PipelineStateCompatible(PipelineStateRecord record)
    {
        var bytes = record.Bytes.AsSpan();

        // Restrict format/topology/sample state to the first observed draw.
        // Other enums in the standard state structures are host-validated;
        // acceptance is not a claim of full console fixed-function parity.
        return record.Kind switch
        {
            14 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 3,
            15 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 28 &&
                  BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]) == 1,
            16 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 20,
            17 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == 1 &&
                  BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == 0,
            _ => true
        };
    }

    private static void VerifyR10VertexFetchExtent()
    {
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
        if (OperatingSystem.IsWindows())
        {
            var vertex = CompileR10Shader(synthetic.VertexSource, "vs_5_1", Console.Out);
            var pixel = CompileR10Shader(synthetic.PixelSource, "ps_5_1", Console.Out);
            var root = SerializeR10HostRoot(synthetic, Console.Out);
            if (vertex == null || pixel == null || root == null)
            {
                throw new InvalidOperationException("R10 synthetic host shader/root serialization failed.");
            }

            Console.WriteLine(
                "[graphics-probe] R10 synthetic host shader compilation and root serialization passed (no GPU draw).");
        }
    }

    [LibraryImport("d3d12.dll", EntryPoint = "D3D12SerializeVersionedRootSignature")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int SerializeR10RootNative(D3D12_VERSIONED_ROOT_SIGNATURE_DESC* description,
        nint* blob, nint* errors);

    [LibraryImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial int CompileR10ShaderNative(void* source, nuint size, byte* name, void* defines,
        void* include,
        byte* entry, byte* target, uint flags1, uint flags2, nint* code, nint* errors);
}
