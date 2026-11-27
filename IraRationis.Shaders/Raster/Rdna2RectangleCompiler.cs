// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Native.Models;
using IraRationis.Shaders.Raster.DualMask;
using IraRationis.Shaders.Raster.Models;
using IraRationis.Shaders.Raster.NativeInstructions;

namespace IraRationis.Shaders.Raster;

// Strict recognizer for a native IA fetch-call / color modulation / rectangle export contract.
// The host IA replaces the separately supplied fetch program, not arbitrary s_swappc instructions.
public static class Rdna2RectangleCompiler
{
    private const string Varyings = """
                                    struct Varyings
                                    {
                                        float4 Position : SV_Position;
                                        float4 Color : COLOR0;
                                        float2 Texcoord : TEXCOORD0;
                                    };
                                    """;

    private const string Vertex = """
                                  Varyings main(float3 position : POSITION0, float4 color : COLOR0, float2 uv : TEXCOORD0)
                                  {
                                      Varyings result;
                                      result.Position = float4(position, 1.0f);
                                      result.Color = color;
                                      result.Texcoord = uv;
                                      return result;
                                  }
                                  """;

    // Find the right-angle corner from the exported screen-aligned positions. Preserve winding.
    // Non-rectangle inputs are left as triangles, rather than inventing a fourth corner.
    private const string Geometry = """
                                    bool IsCorner(float2 a, float2 b, float2 c)
                                    {
                                        return ((a.x == b.x && a.y == c.y && a.y != b.y && a.x != c.x) ||
                                                (a.y == b.y && a.x == c.x && a.x != b.x && a.y != c.y));
                                    }
                                    [maxvertexcount(6)]
                                    void main(triangle Varyings input[3], inout TriangleStream<Varyings> output)
                                    {
                                        uint corner = 3u;
                                        [unroll] for (uint i = 0u; i < 3u; i++)
                                        {
                                            if (IsCorner(input[i].Position.xy, input[(i + 1u) % 3u].Position.xy,
                                                         input[(i + 2u) % 3u].Position.xy))
                                            {
                                                corner = i;
                                            }
                                        }
                                        if (corner == 3u)
                                        {
                                            output.Append(input[0]);
                                            output.Append(input[1]);
                                            output.Append(input[2]);
                                            output.RestartStrip();
                                            return;
                                        }
                                        Varyings a = input[(corner + 2u) % 3u];
                                        Varyings b = input[corner];
                                        Varyings c = input[(corner + 1u) % 3u];
                                        Varyings d;
                                        d.Position = a.Position + c.Position - b.Position;
                                        d.Color = a.Color + c.Color - b.Color;
                                        d.Texcoord = a.Texcoord + c.Texcoord - b.Texcoord;
                                        output.Append(a);
                                        output.Append(b);
                                        output.Append(c);
                                        output.RestartStrip();
                                        output.Append(a);
                                        output.Append(c);
                                        output.Append(d);
                                        output.RestartStrip();
                                    }
                                    """;

    private const string Pixel = """
                                 cbuffer GuestConstants : register(b27, space1)
                                 {
                                     float4 Unused0;
                                     float4 Unused1;
                                     float4 Multiplier;
                                 };
                                 """ + "\n" + PixelArithmetic + "\n" + """
                                                                       float4 main(Varyings input) : SV_Target0
                                                                       {
                                                                           return float4(HalfRtz(LegacyMul(input.Color.x, Multiplier.x)),
                                                                                         HalfRtz(LegacyMul(input.Color.y, Multiplier.y)),
                                                                                         HalfRtz(LegacyMul(input.Color.z, Multiplier.z)),
                                                                                         HalfRtz(LegacyMul(input.Color.w, Multiplier.w)));
                                                                       }
                                                                       """;

    private const string PixelArithmetic = """
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
                                           float LegacyMul(float a, float b)
                                           {
                                               return a == 0.0f || b == 0.0f ? 0.0f : a * b;
                                           }
                                           """;

    // COLOR1.z is RGBA8 UNORM. For each of its 256 values, truncating the
    // captured multiply-add with offsets 0.1, 1.1, 2.1, 3.1 gives 4*byte + offset.
    // The array is lowered to a host CBV with the same contiguous float4 layout.
    private const string IndexedArrayVertex = """
                                              cbuffer VertexConstants : register(b0, space1)
                                              {
                                                  float4 Parameters[1024];
                                              };
                                              IndexedColorVaryings main(float4 color0 : COLOR0, float4 color1 : COLOR1,
                                                                        float2 position : POSITION0)
                                              {
                                                  uint index = (uint)round(color1.z * 255.0f) * 4u;
                                                  float4 transformX = Parameters[index + 2u];
                                                  float4 transformY = Parameters[index + 3u];
                                                  float x = mad(transformX.x, position.x, mad(transformX.y, position.y, transformX.w));
                                                  float y = mad(transformY.x, position.x, mad(transformY.y, position.y, transformY.w));
                                                  IndexedColorVaryings result;
                                                  result.Position = float4(x, y, 0.0f, 1.0f);
                                                  result.Color0 = color0;
                                                  result.Color1 = color1;
                                                  result.Bias = Parameters[index];
                                                  result.Scale = Parameters[index + 1u];
                                                  return result;
                                              }
                                              """;

    private const string IndexedColorVaryings = """
                                                struct IndexedColorVaryings
                                                {
                                                    float4 Position : SV_Position;
                                                    float4 Color0 : COLOR0;
                                                    float4 Color1 : COLOR1;
                                                    float4 Bias : TEXCOORD0;
                                                    float4 Scale : TEXCOORD1;
                                                };
                                                """;

    private const string IndexedColorVertex = """
                                              cbuffer VertexConstants : register(b7, space1)
                                              {
                                                  float4 Bias;
                                                  float4 Scale;
                                                  float4 TransformX;
                                                  float4 TransformY;
                                              };
                                              IndexedColorVaryings main(float4 color0 : COLOR0, float4 color1 : COLOR1,
                                                                        float2 position : POSITION0)
                                              {
                                                  IndexedColorVaryings result;
                                                  float x = mad(TransformX.x, position.x, mad(TransformX.y, position.y, TransformX.w));
                                                  float y = mad(TransformY.x, position.x, mad(TransformY.y, position.y, TransformY.w));
                                                  result.Position = float4(x, y, 0.0f, 1.0f);
                                                  result.Color0 = color0;
                                                  result.Color1 = color1;
                                                  result.Bias = Bias;
                                                  result.Scale = Scale;
                                                  return result;
                                              }
                                              """;

    private const string IndexedColorPixel = """
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
                                             float LegacyMul(float a, float b)
                                             {
                                                 return a == 0.0f || b == 0.0f ? 0.0f : a * b;
                                             }
                                             float4 main(IndexedColorVaryings input) : SV_Target0
                                             {
                                                 float4 color = saturate(mad(input.Scale, input.Color0, input.Bias));
                                                 color.a = LegacyMul(color.a, input.Color1.a);
                                                 return float4(HalfRtz(color.r), HalfRtz(color.g), HalfRtz(color.b), HalfRtz(color.a));
                                             }
                                             """;

    private const string IndexedTransformVaryings = """
                                                    struct Varyings
                                                    {
                                                        float4 Position : SV_Position;
                                                        float4 Color : COLOR0;
                                                        float Opacity : COLOR1;
                                                    };
                                                    """;

    private const string IndexedTransformVertex = """
                                                  cbuffer VertexConstants : register(b5, space1)
                                                  {
                                                      float4 TransformX;
                                                      float4 TransformY;
                                                  };

                                                  Varyings main(float4 color : COLOR0, float4 opacity : COLOR1, float2 position : POSITION0)
                                                  {
                                                      Varyings result;
                                                      precise float x = mad(TransformX.x, position.x, mad(TransformX.y, position.y, TransformX.w));
                                                      precise float y = mad(TransformY.x, position.x, mad(TransformY.y, position.y, TransformY.w));
                                                      result.Position = float4(x, y, 0.0f, 1.0f);
                                                      result.Color = color;
                                                      result.Opacity = opacity.a;
                                                      return result;
                                                  }
                                                  """;

    private const string IndexedTransformPixel = """
                                                 float4 main(Varyings input) : SV_Target0
                                                 {
                                                     return float4(HalfRtz(input.Color.r), HalfRtz(input.Color.g), HalfRtz(input.Color.b),
                                                         HalfRtz(LegacyMul(input.Color.a, input.Opacity)));
                                                 }
                                                 """;

    private const string TexturedVertex = """
                                          cbuffer TransformA : register(b0)
                                          {
                                              float4 A0, A1, A2, A3;
                                          };
                                          cbuffer TransformB : register(b1)
                                          {
                                              float4 B0, B1, B2, B3;
                                          };
                                          float4 LegacyMul4(float4 a, float b)
                                          {
                                              return float4(a.x == 0 || b == 0 ? 0 : a.x*b,
                                                            a.y == 0 || b == 0 ? 0 : a.y*b,
                                                            a.z == 0 || b == 0 ? 0 : a.z*b,
                                                            a.w == 0 || b == 0 ? 0 : a.w*b);
                                          }
                                          Varyings main(float3 position : POSITION0, float4 color : COLOR0, float2 uv : TEXCOORD0)
                                          {
                                              precise float3 p = mad(A0.xyz, position.x, A3.xyz);
                                              p = mad(A1.xyz, position.y, p);
                                              p = mad(A2.xyz, position.z, p);
                                              precise float4 clip = LegacyMul4(B0, p.x);
                                              clip = mad(B1, p.y, clip);
                                              clip = mad(B2, p.z, clip);
                                              clip = clip + B3;
                                              Varyings result;
                                              result.Position = clip;
                                              result.Color = color;
                                              result.Texcoord = uv;
                                              return result;
                                          }
                                          """;

    private const string TexturedPixel = """
                                         Texture2D<float4> GuestTexture : register(t0);
                                         SamplerState GuestSampler : register(s0);
                                         float4 main(Varyings input) : SV_Target0
                                         {
                                             float4 texel = GuestTexture.Sample(GuestSampler, input.Texcoord);
                                             return float4(HalfRtz(LegacyMul(texel.x, input.Color.x)),
                                                           HalfRtz(LegacyMul(texel.y, input.Color.y)),
                                                           HalfRtz(LegacyMul(texel.z, input.Color.z)),
                                                           HalfRtz(LegacyMul(texel.w, input.Color.w)));
                                         }
                                         """;

    // The texture strip shares the two-transform IA vertex contract with the
    // rectangle path, but its pixel program encodes RGB before the FP16 export.
    // It is already a triangle strip, so no geometry expansion is necessary.
    internal static PlanarColorShaderPair BuildSrgbTextureStrip()
    {
        var helpers = PixelArithmetic + "\n";
        const string transfer = """
                                Texture2D<float4> GuestTexture : register(t0);
                                SamplerState GuestSampler : register(s0);

                                float EncodeRgb(float value)
                                {
                                    precise float result = 0.0f;
                                    if (value > asfloat(0x3b4d2e1cu))
                                    {
                                        precise float exponent = LegacyMul(log2(value), asfloat(0x3ed55555u));
                                        result = mad(exp2(exponent), asfloat(0x3f870a3du), asfloat(0xbd6147aeu));
                                    }
                                    else
                                    {
                                        result = LegacyMul(value, asfloat(0x414eb852u));
                                    }

                                    return result;
                                }

                                float4 main(Varyings input) : SV_Target0
                                {
                                    float4 texel = GuestTexture.Sample(GuestSampler, input.Texcoord);
                                    precise float4 color = float4(LegacyMul(texel.x, input.Color.x),
                                        LegacyMul(texel.y, input.Color.y), LegacyMul(texel.z, input.Color.z),
                                        LegacyMul(texel.w, input.Color.w));
                                    return float4(HalfRtz(EncodeRgb(color.x)), HalfRtz(EncodeRgb(color.y)),
                                        HalfRtz(EncodeRgb(color.z)), HalfRtz(color.w));
                                }
                                """;

        return new PlanarColorShaderPair(Varyings + "\n" + TexturedVertex, Varyings + "\n" + helpers + transfer);
    }

    // A separate indexed IA contract. Validate complete containers before lowering;
    // the native descriptor table, not the PC-facing RTS0 range order, selects the CBV.
    public static RectangleShaderPair TranslateIndexedColor(ReadOnlyMemory<byte> vertex,
        ReadOnlyMemory<byte> pixel, ReadOnlyMemory<byte> root)
    {
        var vertexHash = Convert.ToHexString(SHA256.HashData(vertex.Span));
        var pixelHash = Convert.ToHexString(SHA256.HashData(pixel.Span));
        var single = vertexHash == "BB30A2EE0728F40CF7CBB02494179DE4E8850F956AE95773EBE998FD7BFAA9CF" &&
                     pixelHash == "8CBEFFD17DE01EDD34E436FBF6D672A44BC5E63012CF49EF6D798AECE8DEF9B8";
        var array = vertexHash == "1A546262E886D089F19E7AF74BA9EAC4BF403FD424553EA3F71014E435EDAC3F" &&
                    pixelHash == "A33F9DF0EFFCFAD15A393BD879D1EB0F3AB4CE2B3AB2F499B6ED751A4D55C10D";

        var transformOnly = vertexHash == "274269C2A96159981CCF4A8D99B553A9BBEF8F9C5B1C142440BF173313FFC561" &&
                            pixelHash == "465FC098482EA82737E573EFDC3822625F571BEE7C23B856640BE1F4A8B1C432";

        if ((!single && !array && !transformOnly) || Convert.ToHexString(SHA256.HashData(root.Span)) !=
            "DF44DC5144517A00F01BE4238EC63F9463BFFC8A035C2E02E73CA5AE165D9355")
        {
            throw new NotSupportedException("R23 indexed color shader contract differs.");
        }

        var vertexShader = ReadProgram(ShaderContainer.Parse(vertex), NativeShaderStage.Vertex);
        var pixelShader = ReadProgram(ShaderContainer.Parse(pixel), NativeShaderStage.Pixel);
        var register = array ? 0U : transformOnly ? 5U : 7U;

        ReadOnlySpan<uint> layout = [3, 0, 1, register, 8, 0, 0, 2, 500, 265, 6, 0, 1, 0, 11];

        RequireWords(vertexShader.RootLayout, layout, "indexed vertex root layout");
        RequireWords(pixelShader.RootLayout, layout, "indexed pixel root layout");
        RequireWords(vertexShader.UsedResources, [0x302, 0, register, 0, 0x804], "indexed vertex resources");
        RequireWords(pixelShader.UsedResources, [], "indexed pixel resources");

        if (transformOnly)
        {
            RecognizeIndexedTransform(vertexShader.Code, pixelShader.Code);

            return new RectangleShaderPair(IndexedTransformVaryings + "\n" + IndexedTransformVertex, string.Empty,
                IndexedTransformVaryings + "\n" + PixelArithmetic + "\n" + IndexedTransformPixel, register, 1)
            {
                IndexedVertexConstantBytes = 32
            };
        }

        if (vertexShader.Code.Length != (array ? 384 : 320) || pixelShader.Code.Length != 384)
        {
            throw new NotSupportedException("Indexed native instruction extent differs.");
        }

        return new RectangleShaderPair(IndexedColorVaryings + "\n" +
                                       (array ? IndexedArrayVertex : IndexedColorVertex), string.Empty,
            IndexedColorVaryings + "\n" + IndexedColorPixel, register, 1, array);
    }

    private static void RecognizeIndexedTransform(ReadOnlySpan<byte> vertex, ReadOnlySpan<byte> pixel)
    {
        var position = new RasterInstructionReader(vertex);
        position.IdentityHeader();
        position.SwapProgramCounter(4, 4);
        position.Prefetch(3);
        position.Move32(9, 128);
        position.MoveVector32(0, 128);
        position.LoadDescriptor(0, 8, 4, 0);
        position.MoveVector32(1, 242);
        position.Wait(0xc07f);
        position.LoadBuffer(0, 0, 8, 0);
        position.Wait(0x0070);
        position.FusedMultiplyAdd(2, 1, RasterInstructionReader.Vector(13), 3);
        position.FusedMultiplyAdd(3, 5, RasterInstructionReader.Vector(13), 7);
        position.Accumulate(2, 0, 12);
        position.Accumulate(3, 4, 12);
        position.Export(12, 2, 3, 0, 1, true);
        position.Export(32, 4, 5, 6, 7);
        position.Export(33, 8, 9, 10, 11);
        position.End();
        position.Padding();

        var color = new RasterInstructionReader(pixel);
        color.IdentityHeader();
        color.Prefetch(3);
        color.Move32(124, 12);
        color.Interpolate(2, 1, 3, false);
        color.Interpolate(3, 0, 3, false);
        color.Interpolate(4, 0, 0, false);
        color.Interpolate(5, 0, 1, false);
        color.Interpolate(6, 0, 2, false);
        color.Interpolate(2, 1, 3, true);
        color.Interpolate(3, 0, 3, true);
        color.Interpolate(4, 0, 0, true);
        color.Interpolate(5, 0, 1, true);
        color.Interpolate(6, 0, 2, true);
        color.MultiplyLegacy(3, RasterInstructionReader.Vector(3), 2);
        color.ScalarNoOperation();
        color.ScalarNoOperation();
        color.PackHalfTowardZero(0, RasterInstructionReader.Vector(4), 5);
        color.PackHalfTowardZero(1, RasterInstructionReader.Vector(6), 3);
        color.Export(0, 0, 1, 0, 0, true, true, true);
        color.End();
        color.Padding();
    }

    public static RectangleShaderPair Translate(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> pixel,
        ReadOnlyMemory<byte> root)
    {
        var vertexShader = ShaderContainer.Parse(vertex);
        var pixelShader = ShaderContainer.Parse(pixel);

        ValidateRectangleSignatures(vertexShader, pixelShader);

        var rootData = (ShaderContainer.Parse(root).Part("RTS0") ??
                        throw new NotSupportedException("R14 missing root signature.")).Data.Span;

        RequireWords(rootData, [2, 1, 24, 0, 68, 1, 0, 0, 36, 1, 44, 2, 1, 27, 1, 1, uint.MaxValue],
            "root CBV table");

        var vertexProgram = ReadProgram(vertexShader, NativeShaderStage.Vertex);
        var pixelProgram = ReadProgram(pixelShader, NativeShaderStage.Pixel);

        if (DualMaskNativeContract.HasBindingShape(pixelProgram))
        {
            DualMaskNativeContract.Validate(vertexProgram, pixelProgram);
            RecognizeVertex(vertexProgram.Code);

            return new RectangleShaderPair(Varyings + "\n" + Vertex, Varyings + "\n" + Geometry,
                Varyings + "\n" + PixelArithmetic + "\n" + DualMaskRectangleShader.PixelSource, 27, 1,
                SamplesDualMasks: true);
        }

        RequireWords(vertexProgram.RootLayout, [3, 0, 1, 27, 8], "vertex root layout");
        RequireWords(pixelProgram.RootLayout, [3, 0, 1, 27, 8], "pixel root layout");
        RequireWords(vertexProgram.UsedResources, [], "vertex resources");
        RequireWords(pixelProgram.UsedResources, [0x302, 0, 27, 0, 0x804], "pixel resources");

        RecognizeVertex(vertexProgram.Code);
        RecognizePixel(pixelProgram.Code);

        return new RectangleShaderPair(Varyings + "\n" + Vertex, Varyings + "\n" + Geometry,
            Varyings + "\n" + Pixel, 27, 1);
    }

    private static void ValidateRectangleSignatures(ShaderContainer vertexShader, ShaderContainer pixelShader)
    {
        ValidateSignature(vertexShader, "ISG1", [
            ("POSITION", 0U, 0U, 7U), ("COLOR", 0U, 1U, 15U),
            ("TEXCOORD", 0U, 2U, 3U)
        ]);

        ValidateSignature(vertexShader, "OSG1", [
            ("SV_Position", 1U, 0U, 15U), ("COLOR", 0U, 1U, 15U),
            ("TEXCOORD", 0U, 2U, 3U)
        ]);

        ValidateSignature(pixelShader, "ISG1", [
            ("SV_Position", 1U, 0U, 15U), ("COLOR", 0U, 1U, 15U),
            ("TEXCOORD", 0U, 2U, 3U)
        ]);

        ValidateSignature(pixelShader, "OSG1", [("SV_Target", 64U, 0U, 15U)]);
    }

    private static NativeProgram ReadProgram(ShaderContainer container, NativeShaderStage stage)
    {
        var programs = NativeProgramReader.Read(container);

        if (programs.Count != 1 || programs[0].Stage != stage || !programs[0].Wave64 || programs[0].LocalBytes != 0)
        {
            throw new NotSupportedException($"R14 unsupported native {stage} program.");
        }

        return programs[0];
    }

    private static void ValidateSignature(ShaderContainer container, string tag,
        ReadOnlySpan<(string Name, uint System, uint Register, uint Mask)> expected)
    {
        var data = (container.Part(tag) ?? throw new NotSupportedException($"R14 missing {tag}.")).Data.Span;

        if (data.Length < 8 + 32 * expected.Length || Word(data, 0) != (uint)expected.Length || Word(data, 4) != 8)
        {
            throw new NotSupportedException($"R14 unsupported {tag} shape.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var byteOffset = 8 + 32 * index;
            var expectedElement = expected[index];
            var nameOffset = Word(data, byteOffset + 4);

            if (nameOffset < 8 + 32 * expected.Length || nameOffset >= data.Length)
            {
                throw new InvalidDataException($"R14 invalid {tag} semantic offset.");
            }

            var name = data[(int)nameOffset..];
            var end = name.IndexOf((byte)0);

            if (end < 0 || !name[..end].SequenceEqual(Encoding.ASCII.GetBytes(expectedElement.Name)) ||
                Word(data, byteOffset) != 0 || Word(data, byteOffset + 8) != 0
                || Word(data, byteOffset + 12) != expectedElement.System ||
                Word(data, byteOffset + 16) != 3 || Word(data, byteOffset + 20) != expectedElement.Register
                || data[byteOffset + 24] != expectedElement.Mask ||
                Word(data, byteOffset + 28) != 0)
            {
                throw new NotSupportedException($"R14 unsupported {tag} semantic {index}.");
            }
        }
    }

    private static uint Word(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static void RequireWords(ReadOnlySpan<byte> bytes, ReadOnlySpan<uint> expected, string operation)
    {
        if (bytes.Length != expected.Length * sizeof(uint))
        {
            throw new NotSupportedException($"R14 unsupported {operation} length.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (Word(bytes, index * sizeof(uint)) != expected[index])
            {
                throw new NotSupportedException($"R14 unsupported {operation} word {index}.");
            }
        }
    }

    private static void RecognizeVertex(ReadOnlySpan<byte> code)
    {
        // The fetch-call convention supplies v4..v13 from the pipeline input layout.
        RecognizeCode(code, [
            0xbe842104, 0xbfa00003, 0x7e0e02f2, 0x7e040280, 0xbf8c3f70,
            0xf80008cf, 0x07060504, 0xf800020f, 0x0b0a0908, 0xf800021f, 0x07020d0c, 0xbf810000
        ], "vertex");
    }

    private static void RecognizePixel(ReadOnlySpan<byte> code)
    {
        RecognizeCode(code, [
            0xbfa00003, 0xbe820309, 0xbe890380, 0xbefc0302, 0xf4080104, 0xfa000000,
            0xc8080000, 0xc80c0100, 0xc8100200, 0xc8000300, 0xc8090001, 0xc80d0101, 0xc8110201,
            0xc8010301, 0xbf8cc07f, 0xf4280002, 0xfa000020, 0xbf8cc07f, 0x0e040400, 0x0e060601,
            0x0e080802, 0x0e0a0003, 0xbf800000, 0xbf800000, 0x5e000702, 0x5e020b04,
            0xf8001c0f, 0x00000100, 0xbf810000
        ], "pixel");
    }

    private static void RecognizeCode(ReadOnlySpan<byte> code, ReadOnlySpan<uint> live, string stage)
    {
        var offset = 0;
        // VOP3 v_nop with optional literal operand, as in the existing fetch recognizer.
        while (offset <= code.Length - 8 && (Word(code, offset) & 0xffff0000U) == 0xd5800000U)
        {
            var operands = Word(code, offset + 4);

            offset += 8;

            if ((operands & 511U) == 255 || ((operands >> 9) & 511U) == 255 || ((operands >> 18) & 511U) == 255)
            {
                offset += 4;
            }
        }

        var length = live.Length * sizeof(uint);

        if (offset > code.Length || length > code.Length - offset)
        {
            throw new NotSupportedException($"R14 truncated {stage} program.");
        }

        RequireWords(code.Slice(offset, length), live, stage);

        offset += length;

        while (offset < code.Length)
        {
            if (Word(code, offset) != 0xbf9f0000)
            {
                throw new NotSupportedException($"R14 unsupported {stage} padding.");
            }

            offset += 4;
        }
    }

    // R16: lower the independently recognized two-transform / sampled-color contract.
    // Registers in the generated HLSL are host bindings, not console register numbers.
    public static RectangleShaderPair TranslateTextured(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> pixel,
        ReadOnlyMemory<byte> root)
    {
        var vertexShader = ShaderContainer.Parse(vertex);
        var pixelShader = ShaderContainer.Parse(pixel);

        ValidateRectangleSignatures(vertexShader, pixelShader);

        var rootData = (ShaderContainer.Parse(root).Part("RTS0") ??
                        throw new NotSupportedException("R16 missing root signature.")).Data.Span;

        RequireWords(rootData, [2, 1, 24, 0, 68, 1, 0, 0, 36, 1, 44, 2, 1, 27, 1, 1, uint.MaxValue],
            "textured root envelope");

        var vertexProgram = ReadProgram(vertexShader, NativeShaderStage.Vertex);
        var pp = ReadProgram(pixelShader, NativeShaderStage.Pixel);

        const string layout = "B02EC3209766F0B2667FF344AEDE52AB362CF64E6FB087D769870C6FAA942696";

        if (Convert.ToHexString(SHA256.HashData(vertexProgram.RootLayout)) != layout ||
            Convert.ToHexString(SHA256.HashData(pp.RootLayout)) != layout)
        {
            throw new NotSupportedException("R16 unsupported native resource layout.");
        }

        RequireWords(vertexProgram.UsedResources, [
            0x00000300U, 0x00000000U, 0x00000007U, 0x00000040U, 0x00000804U, 0x00000302U,
            0x00000010U, 0x00000000U, 0x00000000U, 0x00000804U
        ], "textured vertex resources");

        RequireWords(pp.UsedResources, [
            0x00000401U, 0x00000000U, 0x0000000eU, 0x00000000U, 0x00020004U, 0x00000300U,
            0x00000008U, 0x00000115U, 0x00000004U, 0x00000808U
        ], "textured pixel resources");

        RecognizeCode(vertexProgram.Code, [
            0xbe842104U, 0xbfa00003U, 0xbe890380U, 0x7e260280U, 0xf4080104U, 0xfa000000U,
            0xf4080004U, 0xfa000040U, 0x7e2402f2U, 0xbf8cc07fU, 0xf4280502U, 0xfa000000U,
            0xf4280402U, 0xfa000020U, 0xf4280602U, 0xfa000010U, 0xf4280702U, 0xfa000030U,
            0xf4300000U, 0xfa000160U, 0xbf8c0070U, 0xd54b000fU, 0x00720814U, 0xd54b0007U,
            0x00760815U, 0xd54b0004U, 0x007a0816U, 0x561e0a18U, 0x560e0a19U, 0x56080a1aU,
            0x561e0c10U, 0x560e0c11U, 0x56080c12U, 0x0e1c1e00U, 0x0e0a1e01U, 0x0e0c1e02U,
            0x0e041e03U, 0x561c0e04U, 0x560a0e05U, 0x560c0e06U, 0x56040e07U, 0x561c0808U,
            0x560a0809U, 0x560c080aU, 0x5604080bU, 0x06061c0cU, 0x060a0a0dU, 0x060c0c0eU,
            0x0608040fU, 0xf80008cfU, 0x04060503U, 0xf800020fU, 0x0b0a0908U, 0xf800021fU,
            0x12130d0cU, 0xbf810000U
        ], "textured vertex");

        RecognizeCode(pp.Code, [
            0xbfa00003U, 0xbe8c047eU, 0xbefe0a7eU, 0xbe820309U, 0xbe890380U, 0xbefc0302U,
            0xf40c0104U, 0xfa000020U, 0xc8080400U, 0xc80c0500U, 0xc8090401U, 0xc80d0501U,
            0x87fe0c7eU, 0xb0000400U, 0xbe8103ffU, 0x00c00000U, 0xbe8203ffU, 0x18f00000U,
            0xbe830380U, 0xc8180000U, 0xbf8cc07fU, 0xf0800f08U, 0x00010b02U, 0xc81c0100U,
            0xc8200200U, 0xc8000300U, 0xc8190001U, 0xc81d0101U, 0xc8210201U, 0xc8010301U,
            0xbf8c3f70U, 0x0e040d0bU, 0x0e060f0cU, 0x0e08110dU, 0x0e0a010eU, 0xbf800000U,
            0xbf800000U, 0x5e000702U, 0x5e020b04U, 0xf8001c0fU, 0x00000100U, 0xbf810000U
        ], "textured pixel");

        // The GS must not invent a fourth clip-space corner if w differs.
        var geometry = Geometry.Replace("if (corner == 3u)",
            "if (corner == 3u || input[0].Position.w != input[1].Position.w || input[0].Position.w != input[2].Position.w)",
            StringComparison.Ordinal);

        return new RectangleShaderPair(Varyings + "\n" + TexturedVertex, Varyings + "\n" + geometry,
            Varyings + "\n" + PixelArithmetic + "\n" + TexturedPixel, 0, 0);
    }
}
