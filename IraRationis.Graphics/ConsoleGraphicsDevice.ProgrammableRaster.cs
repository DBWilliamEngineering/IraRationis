// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using IraRationis.Shaders;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct RasterVertex
    {
        public readonly Vector4 Position, Color, TexCoord;

        public RasterVertex(Vector4 position, Vector4 color, Vector4 texCoord)
        {
            Position = position;
            Color = color;
            TexCoord = texCoord;
        }
    }

    /// <summary>
    /// Reusable, thread-affine host graphics pipeline for explicitly supplied geometry
    /// and RGBA8 textures. Owns the root signature and PSO across render calls.
    /// Render returns tightly packed RGBA8 after confirmed GPU completion.
    /// Does not infer guest memory ownership or modify guest render targets.
    /// </summary>
    public sealed class ProgrammableRasterPipeline : IDisposable
    {
        private const int MaximumDimension = 1024;
        private const int MaximumVertices = 3072;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private nint _device, _queue, _root, _pipeline;
        private bool _disposed;

        public ProgrammableRasterPipeline(MediaGpuContext context, ProgrammableRasterProgram program)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(program);

            context.AcquireDeviceQueue(out _device, out _queue);

            try
            {
                if (_device != (nint)_host || _queue == 0 || sizeof(RasterVertex) != 48)
                {
                    throw new InvalidOperationException("Raster pipeline requires the runtime D3D12 device and a valid vertex ABI.");
                }

                CreateRasterPipeline(program, out _root, out _pipeline);
            }
            catch
            {
                Dispose();

                throw;
            }
        }

        private void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != _owner)
            {
                throw new InvalidOperationException("Raster pipeline is thread-affine.");
            }
        }

        public void Dispose()
        {
            CheckThread();

            if (_disposed)
            {
                return;
            }

            _disposed = true;

            ReleaseR42Object(_pipeline);
            ReleaseR42Object(_root);
            ReleaseR42Object(_queue);
            ReleaseR42Object(_device);

            _pipeline = 0;
            _root = 0;
            _queue = 0;
            _device = 0;
        }

        public byte[] Render(ReadOnlySpan<RasterVertex> vertices, ReadOnlySpan<byte> rgba,
            int textureWidth, int textureHeight, int width, int height)
        {
            CheckThread();
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_device != (nint)_host || vertices.IsEmpty || vertices.Length > MaximumVertices ||
                vertices.Length % 3 != 0 || textureWidth is < 1 or > MaximumDimension ||
                textureHeight is < 1 or > MaximumDimension || width is < 1 or > MaximumDimension ||
                height is < 1 or > MaximumDimension || rgba.Length != checked(textureWidth * textureHeight * 4))
            {
                throw new ArgumentException("Invalid raster draw dimensions, texture or triangle list.");
            }

            foreach (var vertex in vertices)
            {
                if (!ProgrammableRasterProgram.Finite(vertex.Position) ||
                    !ProgrammableRasterProgram.Finite(vertex.Color) ||
                    !ProgrammableRasterProgram.Finite(vertex.TexCoord))
                {
                    throw new ArgumentException("Raster vertices must be finite.", nameof(vertices));
                }
            }

            var vertexBytes = MemoryMarshal.AsBytes(vertices);
            var textureOffset = (vertexBytes.Length + 511) & ~511;
            var texturePitch = (textureWidth * 4 + 255) & ~255;
            var outputPitch = (width * 4 + 255) & ~255;
            var uploadBytes = checked(textureOffset + texturePitch * textureHeight);
            var readbackBytes = checked(outputPitch * height);
            var data = new byte[uploadBytes];

            vertexBytes.CopyTo(data);

            for (var row = 0; row < textureHeight; row++)
            {
                rgba.Slice(row * textureWidth * 4, textureWidth * 4)
                    .CopyTo(data.AsSpan(textureOffset + row * texturePitch));
            }

            nint texture = 0, target = 0, readback = 0, rtvHeap = 0;

            try
            {
                CheckHostResult(CreateHostResource(RasterTexture(textureWidth, textureHeight, 0), 1, 0, &texture),
                    "R67 texture");
                CheckHostResult(CreateHostResource(RasterTexture(width, height, 1), 1, 0, &target), "R67 target");
                CheckHostResult(CreateHostResource(HostBufferDescription((ulong)readbackBytes), 3, CopyDestination,
                    &readback), "R67 readback");

                RequireR42Object(texture, "R67 texture");
                RequireR42Object(target, "R67 target");
                RequireR42Object(readback, "R67 readback");

                rtvHeap = CreateR11ViewHeap(_device, 2);

                using var utility = AcquireUtilityContext((ulong)uploadBytes, 1, versioned: true);

                utility.PrepareVersionedUpload(data);

                var commands = (ID3D12GraphicsCommandList*)utility.List;
                var heap = (ID3D12DescriptorHeap*)utility.Heap;
                var rtv = ((ID3D12DescriptorHeap*)rtvHeap)->GetCPUDescriptorHandleForHeapStart();
                var srv = heap->GetCPUDescriptorHandleForHeapStart();
                var table = heap->GetGPUDescriptorHandleForHeapStart();

                if (rtv.ptr == 0 || srv.ptr == 0 || table.ptr == 0)
                {
                    throw new InvalidOperationException("R67 invalid native descriptor handles.");
                }

                _host->CreateRenderTargetView((ID3D12Resource*)target, null, rtv);
                _host->CreateShaderResourceView((ID3D12Resource*)texture, null, srv);

                TransitionHostResource(utility.List, texture, 0, CopyDestination);
                RecordHostTextureTransfer(utility.List, texture, utility.Upload, (ulong)uploadBytes,
                    new HostTextureTransferFootprint(0, 28, (uint)textureWidth, (uint)textureHeight,
                        (uint)texturePitch, (ulong)textureOffset), upload: true);
                TransitionHostResource(utility.List, texture, CopyDestination, 0x80);
                TransitionHostResource(utility.List, target, 0, 4);

                var clear = stackalloc float[4] { 0, 0, 0, 0 };
                var viewport = new D3D12_VIEWPORT { Width = width, Height = height, MaxDepth = 1.0f };
                var scissor = new RECT { right = width, bottom = height };
                var vertexView = new D3D12_VERTEX_BUFFER_VIEW
                {
                    BufferLocation = ((ID3D12Resource*)utility.Upload)->GetGPUVirtualAddress(),
                    SizeInBytes = (uint)vertexBytes.Length,
                    StrideInBytes = (uint)sizeof(RasterVertex)
                };

                if (vertexView.BufferLocation == 0)
                {
                    throw new InvalidOperationException("R67 invalid vertex GPU address.");
                }

                commands->ClearRenderTargetView(rtv, clear, 0, null);
                commands->SetPipelineState((ID3D12PipelineState*)_pipeline);
                commands->SetGraphicsRootSignature((ID3D12RootSignature*)_root);
                commands->SetDescriptorHeaps(1, &heap);
                commands->SetGraphicsRootDescriptorTable(0, table);
                commands->IASetPrimitiveTopology((D3D_PRIMITIVE_TOPOLOGY)4);
                commands->IASetVertexBuffers(0, 1, &vertexView);
                commands->RSSetViewports(1, &viewport);
                commands->RSSetScissorRects(1, &scissor);
                commands->OMSetRenderTargets(1, &rtv, false, null);
                commands->DrawInstanced((uint)vertices.Length, 1, 0, 0);
                commands->OMSetRenderTargets(0, null, false, null);

                TransitionHostResource(utility.List, target, 4, CopySource);
                RecordHostTextureTransfer(utility.List, target, readback, (ulong)readbackBytes,
                    new HostTextureTransferFootprint(0, 28, (uint)width, (uint)height, (uint)outputPitch, 0),
                    upload: false);
                TransitionHostResource(utility.List, target, CopySource, 0);
                TransitionHostResource(utility.List, texture, 0x80, 0);

                utility.SubmitAndWait(_queue);

                return ReadRasterPixels(readback, width, height, outputPitch);
            }
            finally
            {
                ReleaseR42Object(rtvHeap);
                ReleaseR42Object(readback);
                ReleaseR42Object(target);
                ReleaseR42Object(texture);
            }
        }
    }

    private static ResourceDescription RasterTexture(int width, int height, uint flags)
    {
        return new ResourceDescription
        {
            Dimension = 3,
            Width = (ulong)width,
            Height = (uint)height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 28,
            SampleCount = 1,
            Flags = flags
        };
    }

    private static byte[] ReadRasterPixels(nint readback, int width, int height, int pitch)
    {
        var read = new HostRange { End = (nuint)(pitch * height) };
        var noWrite = default(HostRange);
        var mapped = (void*)null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

        CheckHostResult(map(readback, 0, &read, &mapped), "R67 readback map");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("R67 null readback mapping.");
            }

            var pixels = new byte[checked(width * height * 4)];

            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<byte>((byte*)mapped + row * pitch, width * 4)
                    .CopyTo(pixels.AsSpan(row * width * 4));
            }

            return pixels;
        }
        finally
        {
            unmap(readback, 0, &noWrite);
        }
    }

    private static void CreateRasterPipeline(ProgrammableRasterProgram program, out nint root, out nint pipeline)
    {
        root = 0;
        pipeline = 0;

        var vs = CompileR10Shader(program.EmitVertex(), "vs_5_1", Console.Error)
                 ?? throw new InvalidOperationException("R67 vertex compilation failed.");
        var ps = CompileR10Shader(program.EmitPixel(), "ps_5_1", Console.Error)
                 ?? throw new InvalidOperationException("R67 pixel compilation failed.");
        var range = new D3D12_DESCRIPTOR_RANGE1 { RangeType = 0, NumDescriptors = 1 };
        var parameter = new D3D12_ROOT_PARAMETER1 { ParameterType = 0, ShaderVisibility = (D3D12_SHADER_VISIBILITY)5 };

        parameter.Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
        {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &range
        };

        var sampler = new D3D12_STATIC_SAMPLER_DESC
        {
            Filter = 0,
            AddressU = (D3D12_TEXTURE_ADDRESS_MODE)3,
            AddressV = (D3D12_TEXTURE_ADDRESS_MODE)3,
            AddressW = (D3D12_TEXTURE_ADDRESS_MODE)3,
            MaxAnisotropy = 1,
            ComparisonFunc = (D3D12_COMPARISON_FUNC)1,
            ShaderVisibility = (D3D12_SHADER_VISIBILITY)5
        };
        var signature = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };

        signature.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1
        {
            NumParameters = 1,
            pParameters = &parameter,
            NumStaticSamplers = 1,
            pStaticSamplers = &sampler,
            Flags = (D3D12_ROOT_SIGNATURE_FLAGS)1
        };

        nint blob = 0, errors = 0;
        var nativeRoot = (void*)null;
        var nativePipeline = (void*)null;

        try
        {
            var hr = SerializeR10RootNative(&signature, &blob, &errors);

            if (errors != 0)
            {
                Console.Error.WriteLine(System.Text.Encoding.UTF8.GetString(ReadR10Blob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            CheckHostResult(hr, "R67 root serialization");
            RequireR42Object(blob, "R67 root blob");

            var bytes = ReadR10Blob(blob, 1024 * 1024);
            var rootId = RootSignatureId;

            fixed (byte* pointer = bytes)
            {
                CheckHostResult(_host->CreateRootSignature(0, pointer, (nuint)bytes.Length, &rootId, &nativeRoot),
                    "R67 root signature");
            }

            RequireR42Object((nint)nativeRoot, "R67 root signature");

            var elements = stackalloc D3D12_INPUT_ELEMENT_DESC[3];
            var pipelineId = PipelineId;

            fixed (byte* position = "POSITION\0"u8)
            fixed (byte* color = "COLOR\0"u8)
            fixed (byte* texcoord = "TEXCOORD\0"u8)
            fixed (byte* vertex = vs)
            fixed (byte* pixel = ps)
            {
                elements[0] = RasterInput(position, 0);
                elements[1] = RasterInput(color, 16);
                elements[2] = RasterInput(texcoord, 32);

                var state = new D3D12_GRAPHICS_PIPELINE_STATE_DESC
                {
                    pRootSignature = (ID3D12RootSignature*)nativeRoot,
                    VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vertex, BytecodeLength = (nuint)vs.Length },
                    PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = pixel, BytecodeLength = (nuint)ps.Length },
                    InputLayout = new D3D12_INPUT_LAYOUT_DESC { pInputElementDescs = elements, NumElements = 3 },
                    SampleMask = uint.MaxValue,
                    PrimitiveTopologyType = (D3D12_PRIMITIVE_TOPOLOGY_TYPE)3,
                    NumRenderTargets = 1,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    RasterizerState = new D3D12_RASTERIZER_DESC
                    {
                        FillMode = (D3D12_FILL_MODE)3,
                        CullMode = (D3D12_CULL_MODE)1,
                        DepthClipEnable = true
                    },
                    DepthStencilState = new D3D12_DEPTH_STENCIL_DESC
                    {
                        DepthFunc = (D3D12_COMPARISON_FUNC)8,
                        FrontFace = RasterStencil(),
                        BackFace = RasterStencil()
                    }
                };

                state.RTVFormats.e0 = (DXGI_FORMAT)28;
                state.BlendState.RenderTarget.e0 = new D3D12_RENDER_TARGET_BLEND_DESC
                {
                    SrcBlend = (D3D12_BLEND)2,
                    DestBlend = (D3D12_BLEND)1,
                    BlendOp = (D3D12_BLEND_OP)1,
                    SrcBlendAlpha = (D3D12_BLEND)2,
                    DestBlendAlpha = (D3D12_BLEND)1,
                    BlendOpAlpha = (D3D12_BLEND_OP)1,
                    LogicOp = (D3D12_LOGIC_OP)4,
                    RenderTargetWriteMask = 15
                };

                CheckHostResult(_host->CreateGraphicsPipelineState(&state, &pipelineId, &nativePipeline),
                    "R67 graphics pipeline");
            }

            RequireR42Object((nint)nativePipeline, "R67 graphics pipeline");

            root = (nint)nativeRoot;
            pipeline = (nint)nativePipeline;
            nativeRoot = null;
            nativePipeline = null;
        }
        finally
        {
            ReleaseR42Object((nint)nativePipeline);
            ReleaseR42Object((nint)nativeRoot);
            ReleaseR42Object(errors);
            ReleaseR42Object(blob);
        }
    }

    private static D3D12_INPUT_ELEMENT_DESC RasterInput(byte* name, uint offset)
    {
        return new D3D12_INPUT_ELEMENT_DESC
        {
            SemanticName = (sbyte*)name,
            Format = (DXGI_FORMAT)2,
            AlignedByteOffset = offset
        };
    }

    private static D3D12_DEPTH_STENCILOP_DESC RasterStencil()
    {
        return new D3D12_DEPTH_STENCILOP_DESC
        {
            StencilFailOp = (D3D12_STENCIL_OP)1,
            StencilDepthFailOp = (D3D12_STENCIL_OP)1,
            StencilPassOp = (D3D12_STENCIL_OP)1,
            StencilFunc = (D3D12_COMPARISON_FUNC)8
        };
    }

    // Optional verification of the reusable renderer, not a separate renderer.
    public static void VerifyAuthoredRaster(MediaGpuContext context)
    {
        var program = new ProgrammableRasterProgram(
            [new(RasterExpression.Position), new(RasterExpression.Color), new(RasterExpression.TexCoord)],
            0, 1, 2,
            [new(RasterExpression.TexCoord), new(RasterExpression.Sample, 0), new(RasterExpression.Color),
                new(RasterExpression.Multiply, 1, 2), new(RasterExpression.Saturate, 3)], 4);

        using var renderer = new ProgrammableRasterPipeline(context, program);

        RasterVertex[] vertices =
        [
            new(new Vector4(-1, -1, 0, 1), Vector4.One, new Vector4(0, 1, 0, 0)),
            new(new Vector4(-1, 3, 0, 1), Vector4.One, new Vector4(0, -1, 0, 0)),
            new(new Vector4(3, -1, 0, 1), Vector4.One, new Vector4(2, 1, 0, 0))
        ];
        byte[] texture = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        var first = renderer.Render(vertices, texture, 2, 2, 8, 8);
        var tint = new Vector4(0, 1, 1, 1);

        for (var index = 0; index < vertices.Length; index++)
        {
            vertices[index] = new RasterVertex(vertices[index].Position, tint, vertices[index].TexCoord);
        }

        var second = renderer.Render(vertices, texture, 2, 2, 8, 8);

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var expected = texture.AsSpan(((y / 4) * 2 + x / 4) * 4, 4);
                var offset = (y * 8 + x) * 4;

                if (!first.AsSpan(offset, 4).SequenceEqual(expected) || second[offset] != 0 ||
                    !second.AsSpan(offset + 1, 3).SequenceEqual(expected[1..]))
                {
                    throw new InvalidOperationException($"R67 raster readback mismatch at ({x},{y}).");
                }
            }
        }

        Console.WriteLine("[runtime] IRX-R67: draws=2; pixels=128; textureSampling=verified; tint=verified; pipelineReuse=verified; readback=verified.");
    }
}
