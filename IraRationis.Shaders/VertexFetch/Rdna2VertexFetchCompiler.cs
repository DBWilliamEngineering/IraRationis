// SPDX-License-Identifier: GPL-3.0-or-later
// GFX10 instruction encodings and P0 interpolation semantics cross-checked
// against LLVM AMDGPU and VIctory/victory-fork gfx10 and emit/lower.rs.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Native.Models;
using IraRationis.Shaders.VertexFetch.Models;

namespace IraRationis.Shaders.VertexFetch;

// A deliberately narrow semantic lowering pass. Match the entire live instruction
// sequence and its interfaces, not a container hash. Do not accept arbitrary
// instructions, modifiers, branch targets, signatures or resource layouts.
public static class Rdna2VertexFetchCompiler
{
    private const string PixelSource = """
                                       // P0 means the provoking vertex, not a perspective-interpolated value.
                                       // Convert each component to half precision toward zero, then unpack it
                                       // for the host render-target export. The result is not a packed integer.
                                       float HalfRtz(float value)
                                       {
                                           uint bits = asuint(value);
                                           uint sign = (bits >> 16) & 0x8000u;
                                           uint exponent = (bits >> 23) & 255u;
                                           uint mantissa = bits & 0x7fffffu;
                                           uint halfBits = sign;
                                           if (exponent == 255u)
                                           {
                                               halfBits |= 0x7c00u | (mantissa == 0u ? 0u : ((mantissa >> 13) | 0x0200u));
                                           }
                                           else if (exponent >= 143u)
                                           {
                                               halfBits |= 0x7bffu;
                                           }
                                           else if (exponent >= 113u)
                                           {
                                               halfBits |= ((exponent - 112u) << 10) | (mantissa >> 13);
                                           }
                                           else if (exponent >= 103u)
                                           {
                                               halfBits |= (mantissa | 0x800000u) >> (126u - exponent);
                                           }
                                           return f16tof32(halfBits);
                                       }
                                       float4 main(float4 position : SV_Position, nointerpolation float4 attribute : TEXCOORD0) : SV_Target0
                                       {
                                           return float4(HalfRtz(attribute.x), HalfRtz(attribute.y), HalfRtz(attribute.z), HalfRtz(attribute.w));
                                       }
                                       """;

    public static VertexFetchShaderPair Translate(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> pixel,
        ReadOnlyMemory<byte> root)
    {
        var vertexShader = ShaderContainer.Parse(vertex);
        var pixelShader = ShaderContainer.Parse(pixel);

        ValidateSignature(vertexShader, "ISG1",
            [("SV_VertexID", 0U, 6U, 1U, 0U, 1U)]);

        ValidateSignature(vertexShader, "OSG1",
            [("SV_Position", 0U, 1U, 3U, 0U, 15U), ("TEXCOORD", 0U, 0U, 3U, 1U, 15U)]);

        ValidateSignature(pixelShader, "ISG1",
            [("SV_Position", 0U, 1U, 3U, 0U, 15U), ("TEXCOORD", 0U, 0U, 3U, 1U, 15U)]);

        ValidateSignature(pixelShader, "OSG1",
            [("SV_Target", 0U, 64U, 3U, 0U, 15U)]);

        var (register, space) = ReadRoot(root);

        var vertexProgram = Single(vertexShader, NativeShaderStage.Vertex);
        var pixelProgram = Single(pixelShader, NativeShaderStage.Pixel);
        var fields = RecognizeVertex(vertexProgram);

        RecognizePixel(pixelProgram);

        return new VertexFetchShaderPair(EmitVertex(register, space, fields.Position, fields.Attribute),
            PixelSource, register, space, fields.Position, fields.Attribute);
    }

    private static NativeProgram Single(ShaderContainer container, NativeShaderStage stage)
    {
        var programs = NativeProgramReader.Read(container);

        if (programs.Count != 1 || programs[0].Stage != stage)
        {
            throw new NotSupportedException($"Expected one native {stage} program.");
        }

        return programs[0];
    }

    private static void ValidateSignature(ShaderContainer container, string tag,
        ReadOnlySpan<(string Name, uint Index, uint System, uint Component, uint Register, uint Mask)> expected)
    {
        var part = container.Part(tag) ?? throw new NotSupportedException($"Missing {tag} signature.");
        var data = part.Data.Span;

        if (Word(data, 0) != (uint)expected.Length || Word(data, 4) != 8 || data.Length < 8 + 32 * expected.Length)
        {
            throw new NotSupportedException($"Unsupported {tag} shape.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var byteOffset = 8 + 32 * index;
            var expectedElement = expected[index];
            var nameOffset = Word(data, byteOffset + 4);

            if (nameOffset < 8 + 32 * expected.Length || nameOffset >= data.Length)
            {
                throw new InvalidDataException($"Invalid {tag} semantic offset.");
            }

            var name = data[(int)nameOffset..];
            var end = name.IndexOf((byte)0);

            if (end < 0 || !name[..end].SequenceEqual(Encoding.ASCII.GetBytes(expectedElement.Name)) ||
                Word(data, byteOffset) != 0 || Word(data, byteOffset + 8) != expectedElement.Index
                || Word(data, byteOffset + 12) != expectedElement.System ||
                Word(data, byteOffset + 16) != expectedElement.Component
                || Word(data, byteOffset + 20) != expectedElement.Register ||
                data[byteOffset + 24] != expectedElement.Mask || Word(data, byteOffset + 28) != 0)
            {
                throw new NotSupportedException($"Unsupported {tag} semantic at index {index}.");
            }
        }
    }

    private static (uint Register, uint Space) ReadRoot(ReadOnlyMemory<byte> root)
    {
        var part = ShaderContainer.Parse(root).Part("RTS0") ??
                   throw new NotSupportedException("Missing serialized root signature.");

        var data = part.Data.Span;

        // Version 1.1, one root SRV, all-stage visibility, no static samplers,
        // flags zero. Reject every other root layout until explicitly translated.
        if (data.Length != 48 || Word(data, 0) != 2 || Word(data, 4) != 1 || Word(data, 8) != 24 ||
            Word(data, 12) != 0 || Word(data, 16) != 48 || Word(data, 20) != 0 ||
            Word(data, 24) != 3 || Word(data, 28) != 0 || Word(data, 32) != 36 || Word(data, 44) != 0)
        {
            throw new NotSupportedException("Expected a version 1.1 single-root-SRV signature.");
        }

        return (Word(data, 36), Word(data, 40));
    }

    private static void ValidateLayout(NativeProgram program, bool vertex)
    {
        var layout = program.RootLayout.AsSpan();
        var used = program.UsedResources.AsSpan();

        // One two-DWORD descriptor root at slot zero, user SGPR 8, table offset 0.
        if (!program.Wave64 || program.LocalBytes != 0 || layout.Length != 20 ||
            Word(layout, 0) != 1 || Word(layout, 4) != 0 || Word(layout, 8) != 2 ||
            Word(layout, 12) != 0 || Word(layout, 16) != 8 ||
            (vertex && (used.Length != 20 || Word(used, 0) != 0x100 || Word(used, 4) != 0 ||
                        Word(used, 8) != 0 || Word(used, 12) != 1 || Word(used, 16) != 0x804)) ||
            (!vertex && !used.IsEmpty))
        {
            throw new NotSupportedException("Unsupported native user-data/resource layout.");
        }
    }

    private static (VertexFetchField Position, VertexFetchField Attribute) RecognizeVertex(NativeProgram program)
    {
        ValidateLayout(program, true);

        var reader = new Rdna2VertexFetchCompilerInstructionReader(program.Code);

        reader.SkipPrologue();

        // Build raw buffer descriptor from the two root SRV SGPRs. s2 holds
        // the unbounded record count; s3 fixes the buffer's data interpretation.
        reader.Expect("raw root SRV descriptor", [
            0xbe8303ff, 0x11014fac, 0xbe8203c1, 0xbe800308,
            0x8801ff09, 0x00010000
        ]);

        var position = ReadFetch(ref reader, false);
        var attribute = ReadFetch(ref reader, true);

        reader.Expect("position.w / wait / position and parameter exports", [
            0x7e0002f2, 0xbf8c3f70,
            0xf80008cf, 0x00030201, 0xf800020f, 0x07060504
        ]);

        reader.End();

        return (position, attribute);
    }

    private static VertexFetchField ReadFetch(ref Rdna2VertexFetchCompilerInstructionReader reader, bool attribute)
    {
        if (!attribute)
        {
            reader.Expect("initialize position", [0x7e020280]);
        }

        reader.Expect("load packed vertex-fetch header", [0xf4200100]);

        var operand = reader.Read();
        // Immediate byte offset, no scalar offset register. The two possible
        // headers are read from the same raw root view; allow differing offsets.
        if ((operand & 0xffe00000U) != 0xfa000000U || (operand & 3U) != 0)
        {
            throw new NotSupportedException("Unsupported scalar buffer-load addressing.");
        }

        var offset = operand & 0x1fffffU;

        if (attribute)
        {
            reader.Expect("initialize attribute", [0x7e080280, 0x7e0a0280, 0x7e0c0280, 0x7e0e0280]);
        }
        else
        {
            reader.Expect("initialize position remainder", [0x7e040280, 0x7e060280]);
        }

        reader.Expect("wait / component count / branch", [
            0xbf8cc07f, 0x87058f04,
            attribute ? 0xbf078405U : 0xbf078305U, 0xbf85000a
        ]);

        reader.Expect("extract byte stride and base", [
            0x90058304, 0x90048704, 0x8705bc05,
            0x8704ff04, 0x01fffffc
        ]);

        if (attribute)
        {
            reader.Expect("vertex ID * stride + base / load four DWORDs", [
                0xd5690000, 0x00020005,
                0x4a000004, 0xe0381000, 0x80000400
            ]);
        }
        else
        {
            reader.Expect("vertex ID * stride + base / load three DWORDs", [
                0xd5690001, 0x00020005,
                0x4a020204, 0xe03c1000, 0x80000101
            ]);
        }

        return new VertexFetchField(offset, attribute ? 4U : 3U);
    }

    private static void RecognizePixel(NativeProgram program)
    {
        ValidateLayout(program, false);

        var reader = new Rdna2VertexFetchCompilerInstructionReader(program.Code);

        reader.SkipPrologue();

        reader.Expect("interpolation setup / P0 attribute 0 xyzw", [
            0xbefc030a, 0xc80a0002, 0xc80e0102,
            0xc8120202, 0xc8160302
        ]);

        reader.Expect("interpolation wait", [0xbf800000, 0xbf800000]);

        reader.Expect("pack RTZ half pairs / compressed MRT0 export", [
            0x5e000702, 0x5e020b04,
            0xf8001c0f, 0x00000100
        ]);

        reader.End();
    }

    private static string EmitVertex(uint register, uint space, VertexFetchField position, VertexFetchField attribute)
    {
        return string.Create(CultureInfo.InvariantCulture, $$"""
                                                             ByteAddressBuffer Guest : register(t{{register}}, space{{space}});
                                                             struct Varyings
                                                             {
                                                                 float4 Position : SV_Position;
                                                                 nointerpolation float4 Attribute : TEXCOORD0;
                                                             };
                                                             uint FetchAddress(uint header, uint vertexId)
                                                             {
                                                                 return ((header >> 7) & 0x01fffffcu) + vertexId * ((header >> 3) & 60u);
                                                             }
                                                             Varyings main(uint vertexId : SV_VertexID)
                                                             {
                                                                 uint p = Guest.Load({{position.HeaderOffset}}u);
                                                                 uint a = Guest.Load({{attribute.HeaderOffset}}u);
                                                                 float3 position = float3(0, 0, 0);
                                                                 float4 attribute = float4(0, 0, 0, 0);
                                                                 if ((p & 15u) == {{position.Components}}u)
                                                                 {
                                                                     position = asfloat(Guest.Load3(FetchAddress(p, vertexId)));
                                                                 }
                                                                 if ((a & 15u) == {{attribute.Components}}u)
                                                                 {
                                                                     attribute = asfloat(Guest.Load4(FetchAddress(a, vertexId)));
                                                                 }
                                                                 Varyings result;
                                                                 result.Position = float4(position, 1.0f);
                                                                 result.Attribute = attribute;
                                                                 return result;
                                                             }
                                                             """);
    }

    // Compute the conservative source range required by this recognized shader.
    // The caller must obtain the header at the correct execution boundary;
    // recording-time CPU data is not proof of GPU-visible resource coherence.
    public static bool TryGetReadExtent(VertexFetchShaderPair pair, ReadOnlySpan<byte> header,
        uint firstVertex, uint vertexCount, ulong maximumBytes, out ulong bytes)
    {
        bytes = 0;

        if (vertexCount == 0 || (ulong)firstVertex + vertexCount - 1 > uint.MaxValue)
        {
            return false;
        }

        var last = (ulong)firstVertex + vertexCount - 1;

        foreach (var field in new[] { pair.Position, pair.Attribute })
        {
            if ((ulong)field.HeaderOffset + 4 > (ulong)header.Length)
            {
                return false;
            }

            bytes = Math.Max(bytes, (ulong)field.HeaderOffset + 4);

            var packed = Word(header, (int)field.HeaderOffset);

            if ((packed & 15U) != field.Components)
            {
                continue;
            }

            var address = ((packed >> 7) & 0x01fffffcU) + last * ((packed >> 3) & 60U);

            if (address > uint.MaxValue || address + 4UL * field.Components > 1UL << 32)
            {
                return false;
            }

            bytes = Math.Max(bytes, address + 4UL * field.Components);
        }

        return bytes <= maximumBytes;
    }

    // Synthetic instruction fixture with different header offsets and an
    // independently constructed root layout. No title container is embedded.
    public static VertexFetchShaderPair VerifySyntheticRecognition()
    {
        var layout = Words([1, 0, 2, 0, 8]);
        var used = Words([0x100, 0, 0, 1, 0x804]);

        var vertexCode = Words([
            0xd5800000, 0x03fc0000, 0, 0xbfa00000,
            0xbe8303ff, 0x11014fac, 0xbe8203c1, 0xbe800308, 0x8801ff09, 0x00010000,
            0x7e020280, 0xf4200100, 0xfa000020, 0x7e040280, 0x7e060280,
            0xbf8cc07f, 0x87058f04, 0xbf078305, 0xbf85000a, 0x90058304, 0x90048704,
            0x8705bc05, 0x8704ff04, 0x01fffffc, 0xd5690001, 0x00020005, 0x4a020204,
            0xe03c1000, 0x80000101,
            0xf4200100, 0xfa000024, 0x7e080280, 0x7e0a0280, 0x7e0c0280, 0x7e0e0280,
            0xbf8cc07f, 0x87058f04, 0xbf078405, 0xbf85000a, 0x90058304, 0x90048704,
            0x8705bc05, 0x8704ff04, 0x01fffffc, 0xd5690000, 0x00020005, 0x4a000004,
            0xe0381000, 0x80000400, 0x7e0002f2, 0xbf8c3f70, 0xf80008cf, 0x00030201,
            0xf800020f, 0x07060504, 0xbf810000, 0xbf9f0000
        ]);

        var pixelCode = Words([
            0xbefc030a, 0xc80a0002, 0xc80e0102, 0xc8120202, 0xc8160302,
            0xbf800000, 0xbf800000, 0x5e000702, 0x5e020b04, 0xf8001c0f, 0x00000100, 0xbf810000
        ]);

        var vertex = new NativeProgram(NativeShaderStage.Vertex, true, 8, 12, 0, layout, used, null, vertexCode);
        var pixel = new NativeProgram(NativeShaderStage.Pixel, true, 6, 13, 0, layout, [], null, pixelCode);
        var fields = RecognizeVertex(vertex);

        RecognizePixel(pixel);

        if (fields.Position != new VertexFetchField(32, 3) || fields.Attribute != new VertexFetchField(36, 4))
        {
            throw new InvalidOperationException("Synthetic vertex fetch recognition failed.");
        }

        var rejected = false;
        var bad = (byte[])pixelCode.Clone();

        bad[4] ^= 1; // modify one interpolation operand, not a container hash

        try
        {
            RecognizePixel(pixel with
            {
                Code = bad
            });
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }

        if (!rejected)
        {
            throw new InvalidOperationException("Unsupported pixel variation was accepted.");
        }

        return new VertexFetchShaderPair(EmitVertex(7, 2, fields.Position, fields.Attribute),
            PixelSource, 7, 2, fields.Position, fields.Attribute);
    }

    private static byte[] Words(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[checked(words.Length * 4)];

        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), words[index]);
        }

        return bytes;
    }

    internal static uint Word(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset > data.Length - 4)
        {
            throw new InvalidDataException("Truncated shader metadata.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }
}
