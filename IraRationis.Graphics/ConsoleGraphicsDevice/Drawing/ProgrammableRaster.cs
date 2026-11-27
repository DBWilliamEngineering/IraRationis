// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Drawing;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Media;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Shaders.Raster;
using IraRationis.Shaders.Raster.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{

    internal static ResourceDescription RasterTexture(int width, int height, uint flags)
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

    internal static byte[] ReadRasterPixels(nint readback, int width, int height, int pitch, int bytesPerPixel = 4)
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

            var rowBytes = checked(width * bytesPerPixel);
            var pixels = new byte[checked(rowBytes * height)];

            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<byte>((byte*)mapped + row * pitch, rowBytes)
                    .CopyTo(pixels.AsSpan(row * rowBytes));
            }

            return pixels;
        }
        finally
        {
            unmap(readback, 0, &noWrite);
        }
    }

    internal static void CreateRasterPipeline(ProgrammableRasterProgram program, out nint root, out nint pipeline)
    {
        root = 0;
        pipeline = 0;

        var vertexShader = CompileHostShader(program.EmitVertex(), "vs_5_1", Console.Error)
                 ?? throw new InvalidOperationException("R67 vertex compilation failed.");
        var pixelShader = CompileHostShader(program.EmitPixel(), "ps_5_1", Console.Error)
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
            var resultCode = SerializeRootNative(&signature, &blob, &errors);

            if (errors != 0)
            {
                Console.Error.WriteLine(System.Text.Encoding.UTF8.GetString(ReadShaderBlob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            CheckHostResult(resultCode, "R67 root serialization");
            RequireHostObject(blob, "R67 root blob");

            var bytes = ReadShaderBlob(blob, 1024 * 1024);
            var rootId = RootSignatureId;

            fixed (byte* pointer = bytes)
            {
                CheckHostResult(_host->CreateRootSignature(0, pointer, (nuint)bytes.Length, &rootId, &nativeRoot),
                    "R67 root signature");
            }

            RequireHostObject((nint)nativeRoot, "R67 root signature");

            var elements = stackalloc D3D12_INPUT_ELEMENT_DESC[3];
            var pipelineId = PipelineId;

            fixed (byte* position = "POSITION\0"u8)
            fixed (byte* color = "COLOR\0"u8)
            fixed (byte* texcoord = "TEXCOORD\0"u8)
            fixed (byte* vertex = vertexShader)
            fixed (byte* pixel = pixelShader)
            {
                elements[0] = RasterInput(position, 0);
                elements[1] = RasterInput(color, 16);
                elements[2] = RasterInput(texcoord, 32);

                var state = new D3D12_GRAPHICS_PIPELINE_STATE_DESC
                {
                    pRootSignature = (ID3D12RootSignature*)nativeRoot,
                    VS = new D3D12_SHADER_BYTECODE { pShaderBytecode = vertex, BytecodeLength = (nuint)vertexShader.Length },
                    PS = new D3D12_SHADER_BYTECODE { pShaderBytecode = pixel, BytecodeLength = (nuint)pixelShader.Length },
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

            RequireHostObject((nint)nativePipeline, "R67 graphics pipeline");

            root = (nint)nativeRoot;
            pipeline = (nint)nativePipeline;
            nativeRoot = null;
            nativePipeline = null;
        }
        finally
        {
            ReleaseHostObject((nint)nativePipeline);
            ReleaseHostObject((nint)nativeRoot);
            ReleaseHostObject(errors);
            ReleaseHostObject(blob);
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

        for (var coordinateY = 0; coordinateY < 8; coordinateY++)
        {
            for (var coordinateX = 0; coordinateX < 8; coordinateX++)
            {
                var expected = texture.AsSpan(((coordinateY / 4) * 2 + coordinateX / 4) * 4, 4);
                var offset = (coordinateY * 8 + coordinateX) * 4;

                if (!first.AsSpan(offset, 4).SequenceEqual(expected) || second[offset] != 0 ||
                    !second.AsSpan(offset + 1, 3).SequenceEqual(expected[1..]))
                {
                    throw new InvalidOperationException($"R67 raster readback mismatch at ({coordinateX},{coordinateY}).");
                }
            }
        }

        Console.WriteLine("[runtime] IRX-R67: draws=2; pixels=128; textureSampling=verified; tint=verified; pipelineReuse=verified; readback=verified.");
    }
}
