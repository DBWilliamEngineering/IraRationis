// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Raster;

public static class IndexedUiCompiler
{
    public const string VertexHash = "1CC31822133D45A29549294825B8E3AA09EE1735D794E808F91C4A7A03985E85";
    public const string PixelHash = "0B02670BFF967D1208E013E5750134321D18BE90F905EDCAEC4C369E2BAD5ECF";
    public const string RootHash = "DF44DC5144517A00F01BE4238EC63F9463BFFC8A035C2E02E73CA5AE165D9355";

    public const string PlainVertexHash = "DA7A336AB58A455C0A64D573B51CD800F86D8E6AE53E5247315CF01B3FED6737";
    public const string PlainPixelHash = "F060DE150448E708BD7D71BE21A5654F8C04471DFFB529815FED7471EC745397";

    public const string AlphaVertexHash = "3894C7D633149F71CC98B6F25AFF33CBA31958AB2D3B1D6FC0C34244944AAEFA";
    public const string AlphaPixelHash = "605A48C19028F3C4440C22FDAF2C5084F563096EE8C88E1B7BF0C691AB586975";

    public const string DualAlphaVertexHash = "975E190ECE3AA6D27BE7046CDE3655711251999FC419CDC652A719988E0A4D12";
    public const string DualAlphaPixelHash = "6B179DE1441A847940AD14DDA3C1DC01AAE98ADAFBD5234DC820DD21D2530271";

    public const string BatchedMaskVertexHash = "7074535E90B5965F1FD08541A283AD8E29AE3EE7F3D54826071A3D5D96247471";
    public const string BatchedMaskPixelHash = "CDF9A55BF40E138C3825D3807D0C4D97DFE93DA53D0127AF47EF40D694EFCA5A";
    public const int BatchedMaskConstantBytes = 96;
    public const int BatchedMaskMaximumBatches = 256;

    public const string TransformedTextureVertexHash =
        "3664FD683B8E2E256956490F92A6A5C4809A472FC4339A34DA6C7456FC7EA8AA";
    public const string TransformedTexturePixelHash =
        "1695C915FFBDAE8B818D5910CC8EDB84254A40485688E232AA4A6B81561D1194";

    public const string BatchedDualTextureVertexHash =
        "7042561BD0DE95122A0BDFB639B45B9AAE16CA202BFF4F80E34F1184CF782260";
    public const string BatchedDualTexturePixelHash =
        "51C2EDA1B7FC7B321AC3DA58C53B39154642FC00A4CC80AE85983FB32014DA6F";

    private const string Varyings = """
                                    struct Varyings
                                    {
                                        float4 color : COLOR0;
                                        float4 addend : TEXCOORD0;
                                        float4 multiplier : TEXCOORD1;
                                        float2 uv : TEXCOORD2;
                                        float4 position : SV_Position;
                                    };
                                    """;

    public const string VertexSource = Varyings + """

                                                  cbuffer VertexParameters : register(b0)
                                                  {
                                                      float4 addend;
                                                      float4 multiplier;
                                                      float4 clipX;
                                                      float4 clipY;
                                                      float4 textureX;
                                                      float4 textureY;
                                                  };
                                                  Varyings main(float4 color : COLOR0, float2 position : POSITION0)
                                                  {
                                                      Varyings output;
                                                      output.color = color;
                                                      output.addend = addend;
                                                      output.multiplier = multiplier;
                                                      output.position = float4(mad(clipX.x, position.x, mad(clipX.y, position.y, clipX.w)),
                                                          mad(clipY.x, position.x, mad(clipY.y, position.y, clipY.w)), 0, 1);
                                                      output.uv = float2(mad(textureX.x, position.x, mad(textureX.y, position.y, textureX.w)),
                                                          mad(textureY.x, position.x, mad(textureY.y, position.y, textureY.w)));
                                                      return output;
                                                  }
                                                  """;

    private const string HalfExport = """
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
                                      """;

    public const string PixelSource = Varyings + """

                                                 Texture2DArray<float4> noiseTexture : register(t0);
                                                 Texture2D<float4> colorTexture : register(t1);
                                                 SamplerState colorSampler : register(s0);
                                                 cbuffer PixelParameters : register(b1)
                                                 {
                                                     float4 noiseParameters;
                                                 };
                                                 """ + HalfExport + """
                                                                    float4 main(Varyings input) : SV_Target0
                                                                    {
                                                                        int2 xy = int2(input.position.xy) & 63;
                                                                        int layer = int(noiseParameters.x) & 31;
                                                                        float noise = noiseTexture.Load(int4(xy, layer, 0)).x;
                                                                        float centered = mad(noise, 2.0, -1.0);
                                                                        float magnitude = (1.0 - sqrt(max(0.0, 1.0 - abs(centered)))) * asfloat(0x3c008081u);
                                                                        float dither = centered == 0.0 ? 0.0 : (centered < 0.0 ? -magnitude : magnitude);
                                                                        float4 color = saturate(mad(colorTexture.Sample(colorSampler, input.uv), input.multiplier, input.addend));
                                                                        float4 result = float4(color.rgb + dither, mad(color.a, input.color.a, dither));
                                                                        return float4(HalfRtz(result.r), HalfRtz(result.g), HalfRtz(result.b), HalfRtz(result.a));
                                                                    }
                                                                    """;

    public const string PlainPixelSource = Varyings + """

                                                      Texture2D<float4> colorTexture : register(t0);
                                                      SamplerState colorSampler : register(s0);
                                                      """ + HalfExport + """
                                                                         float4 main(Varyings input) : SV_Target0
                                                                         {
                                                                             float4 color = saturate(mad(colorTexture.Sample(colorSampler, input.uv), input.multiplier, input.addend));
                                                                             float4 result = float4(color.rgb, color.a * input.color.a);
                                                                             return float4(HalfRtz(result.r), HalfRtz(result.g), HalfRtz(result.b), HalfRtz(result.a));
                                                                         }
                                                                         """;

    private const string AlphaVaryings = """
                                         struct Varyings
                                         {
                                             float2 uv : TEXCOORD0;
                                             float4 color : COLOR0;
                                             float4 position : SV_Position;
                                         };
                                         """;

    public const string TransformedTextureVertexSource = AlphaVaryings + """
                                                                         cbuffer VertexParameters : register(b0)
                                                                         {
                                                                             float4 clipX;
                                                                             float4 clipY;
                                                                             float4 textureX;
                                                                             float4 textureY;
                                                                         };

                                                                         Varyings main(float4 color : COLOR0, float2 position : POSITION0)
                                                                         {
                                                                             Varyings output;
                                                                             output.color = color;
                                                                             output.position = float4(mad(clipX.x, position.x, mad(clipX.y, position.y, clipX.w)),
                                                                                 mad(clipY.x, position.x, mad(clipY.y, position.y, clipY.w)), 0, 1);
                                                                             output.uv = float2(mad(textureX.x, position.x, mad(textureX.y, position.y, textureX.w)),
                                                                                 mad(textureY.x, position.x, mad(textureY.y, position.y, textureY.w)));
                                                                             return output;
                                                                         }
                                                                         """;

    public const string TransformedTexturePixelSource = AlphaVaryings + """
                                                                        Texture2D<float4> colorTexture : register(t0);
                                                                        SamplerState colorSampler : register(s0);
                                                                        """ + HalfExport + """
        float4 main(Varyings input) : SV_Target0
        {
            float4 texel = colorTexture.Sample(colorSampler, input.uv);
            float opacity = texel.a == 0.0 || input.color.a == 0.0 ? 0.0 : texel.a * input.color.a;
            return float4(HalfRtz(texel.r), HalfRtz(texel.g), HalfRtz(texel.b), HalfRtz(opacity));
        }
        """;

    public const string AlphaVertexSource = AlphaVaryings + """
                                                            cbuffer VertexParameters : register(b0)
                                                            {
                                                                float4 addend;
                                                                float4 multiplier;
                                                                float4 clipX;
                                                                float4 clipY;
                                                            };
                                                            Varyings main(float4 color : COLOR0, float2 uv : TEXCOORD0, float2 position : POSITION0)
                                                            {
                                                                Varyings output;
                                                                output.uv = uv;
                                                                output.color = mad(multiplier, color, addend);
                                                                output.position = float4(mad(clipX.x, position.x, mad(clipX.y, position.y, clipX.w)),
                                                                    mad(clipY.x, position.x, mad(clipY.y, position.y, clipY.w)), 0, 1);
                                                                return output;
                                                            }
                                                            """;

    public const string AlphaPixelSource = AlphaVaryings + """
                                                           Texture2D<float4> colorTexture : register(t0);
                                                           SamplerState colorSampler : register(s0);
                                                           """ + HalfExport + """
                                                                              float4 main(Varyings input) : SV_Target0
                                                                              {
                                                                                  float alpha = colorTexture.Sample(colorSampler, input.uv).a;
                                                                                  // Legacy multiply returns zero for a zero operand, even with infinity.
                                                                                  float opacity = alpha == 0.0 || input.color.a == 0.0 ? 0.0 : alpha * input.color.a;
                                                                                  return float4(HalfRtz(input.color.r), HalfRtz(input.color.g), HalfRtz(input.color.b), HalfRtz(opacity));
                                                                              }
                                                                              """;

    private const string DualAlphaVaryings = """
                                             struct Varyings
                                             {
                                                 float2 clipUv : TEXCOORD0;
                                                 float2 sourceUv : TEXCOORD1;
                                                 float4 color : COLOR0;
                                                 float4 position : SV_Position;
                                             };
                                             """;

    // The native position export uses the last two rows, loaded from vertex constants at byte 64.
    public const string DualAlphaVertexSource = DualAlphaVaryings + """
                                                                    cbuffer VertexParameters : register(b0)
                                                                    {
                                                                        float4 addend;
                                                                        float4 multiplier;
                                                                        float4 textureX;
                                                                        float4 textureY;
                                                                        float4 clipX;
                                                                        float4 clipY;
                                                                    };

                                                                    Varyings main(float4 color : COLOR0, float2 uv : TEXCOORD0, float2 position : POSITION0)
                                                                    {
                                                                        Varyings output;
                                                                        precise float x = mad(clipX.x, position.x, mad(clipX.y, position.y, clipX.w));
                                                                        precise float y = mad(clipY.x, position.x, mad(clipY.y, position.y, clipY.w));
                                                                        output.position = float4(x, y, 0.0f, 1.0f);
                                                                        output.color = mad(multiplier, color, addend);
                                                                        output.sourceUv = uv;
                                                                        output.clipUv = float2(mad(textureX.x, x, mad(textureX.y, y, textureX.w)),
                                                                                               mad(textureY.x, x, mad(textureY.y, y, textureY.w)));
                                                                        return output;
                                                                    }
                                                                    """;

    public const string DualAlphaPixelSource = DualAlphaVaryings + """
                                                                   Texture2D<float4> sourceMask : register(t0);
                                                                   Texture2D<float4> clipMask : register(t1);
                                                                   SamplerState maskSampler : register(s0);
                                                                   """ + HalfExport + """
        float LegacyAlphaMultiply(float first, float second)
        {
            return first == 0.0f || second == 0.0f ? 0.0f : first * second;
        }

        float4 main(Varyings input) : SV_Target0
        {
            float sourceAlpha = sourceMask.Sample(maskSampler, input.sourceUv).a;
            float clipAlpha = clipMask.Sample(maskSampler, input.clipUv).a;
            precise float sourceOpacity = LegacyAlphaMultiply(sourceAlpha, input.color.a);
            precise float opacity = LegacyAlphaMultiply(sourceOpacity, clipAlpha);
            return float4(HalfRtz(input.color.r), HalfRtz(input.color.g), HalfRtz(input.color.b), HalfRtz(opacity));
        }
        """;

    private const string BatchedMaskVaryings = """
                                               struct Varyings
                                               {
                                                   float4 color : COLOR0;
                                                   float opacity : COLOR1;
                                                   float4 addend : TEXCOORD0;
                                                   float4 multiplier : TEXCOORD1;
                                                   float2 maskUv : TEXCOORD2;
                                                   float4 position : SV_Position;
                                               };
                                               """;

    // COLOR1.b is UNORM8. Truncating the six guest MADs (scale 1530.06,
    // addends 0.1 through 5.1) gives six consecutive float4 rows for each byte value.
    public const string BatchedMaskVertexSource = BatchedMaskVaryings + """
                                                                        struct BatchParameters
                                                                        {
                                                                            float4 addend;
                                                                            float4 multiplier;
                                                                            float4 maskX;
                                                                            float4 maskY;
                                                                            float4 clipX;
                                                                            float4 clipY;
                                                                        };

                                                                        cbuffer VertexParameters : register(b0)
                                                                        {
                                                                            BatchParameters batches[256];
                                                                        };

                                                                        float TransformAxis(float4 parameters, float2 position)
                                                                        {
                                                                            return mad(parameters.x, position.x, mad(parameters.y, position.y, parameters.w));
                                                                        }

                                                                        Varyings main(float4 color : COLOR0, float4 batchAndOpacity : COLOR1, float2 position : POSITION0)
                                                                        {
                                                                            uint batchIndex = (uint)round(batchAndOpacity.b * 255.0f);
                                                                            BatchParameters batch = batches[batchIndex];
                                                                            precise float2 clip = float2(TransformAxis(batch.clipX, position), TransformAxis(batch.clipY, position));
                                                                            Varyings output;
                                                                            output.color = color;
                                                                            output.opacity = batchAndOpacity.a;
                                                                            output.addend = batch.addend;
                                                                            output.multiplier = batch.multiplier;
                                                                            output.maskUv = float2(TransformAxis(batch.maskX, clip), TransformAxis(batch.maskY, clip));
                                                                            output.position = float4(clip, 0.0f, 1.0f);
                                                                            return output;
                                                                        }
                                                                        """;

    public const string BatchedMaskPixelSource = BatchedMaskVaryings + """
                                                                       Texture2D<float4> maskTexture : register(t0);
                                                                       SamplerState maskSampler : register(s0);
                                                                       """ + HalfExport + """
        float LegacyMultiply(float first, float second)
        {
            return first == 0.0f || second == 0.0f ? 0.0f : first * second;
        }

        float4 main(Varyings input) : SV_Target0
        {
            float4 color = saturate(mad(input.multiplier, input.color, input.addend));
            float mask = maskTexture.Sample(maskSampler, input.maskUv).a;
            precise float vertexOpacity = LegacyMultiply(color.a, input.opacity);
            precise float opacity = LegacyMultiply(vertexOpacity, mask);
            return float4(HalfRtz(color.r), HalfRtz(color.g), HalfRtz(color.b), HalfRtz(opacity));
        }
        """;

    private const string BatchedDualTextureVaryings = """
        struct Varyings
        {
            float4 color : COLOR0;
            float4 batchAndOpacity : COLOR1;
            float4 addend : TEXCOORD0;
            float4 multiplier : TEXCOORD1;
            float4 position : SV_Position;
        };
        """;

    public const string BatchedDualTextureVertexSource = BatchedDualTextureVaryings + """
        struct BatchParameters
        {
            float4 addend;
            float4 multiplier;
            float4 clipX;
            float4 clipY;
        };

        cbuffer VertexParameters : register(b0)
        {
            BatchParameters batches[256];
        };

        float TransformAxis(float4 parameters, float2 position)
        {
            return mad(parameters.x, position.x, mad(parameters.y, position.y, parameters.w));
        }

        Varyings main(float4 color : COLOR0, float4 batchAndOpacity : COLOR1, float2 position : POSITION0)
        {
            uint batchIndex = (uint)round(batchAndOpacity.b * 255.0f);
            BatchParameters batch = batches[batchIndex];
            precise float2 clip = float2(TransformAxis(batch.clipX, position), TransformAxis(batch.clipY, position));

            Varyings output;
            output.color = color;
            output.batchAndOpacity = batchAndOpacity;
            output.addend = batch.addend;
            output.multiplier = batch.multiplier;
            output.position = float4(clip, 0.0f, 1.0f);

            return output;
        }
        """;

    public const string BatchedDualTexturePixelSource = BatchedDualTextureVaryings + """
        Texture2D<float4> selectionTexture : register(t0);
        Texture2D<float4> coverageTexture : register(t1);
        cbuffer PixelParameters : register(b1)
        {
            float coverageThreshold;
            float coverageOpacity;
            float selectionId;
            float reserved;
            float2 screenExtent;
            float2 textureExtent;
        };
        """ + HalfExport + """
        float LegacyMultiply(float first, float second)
        {
            return first == 0.0f || second == 0.0f ? 0.0f : first * second;
        }

        float4 main(Varyings input) : SV_Target0
        {
            float4 color = saturate(mad(input.multiplier, input.color, input.addend));
            precise float opacity = LegacyMultiply(color.a, input.batchAndOpacity.a);
            precise float2 inverseExtent = rcp(screenExtent);
            precise float2 screenUv = float2(LegacyMultiply(input.position.x, inverseExtent.x),
                LegacyMultiply(input.position.y, inverseExtent.y));
            precise float2 texel = float2(LegacyMultiply(screenUv.x, textureExtent.x),
                LegacyMultiply(screenUv.y, textureExtent.y));
            int3 sampleAddress = int3((int2)texel, 0);
            uint2 selectionBytes = (uint2)(selectionTexture.Load(sampleAddress).ba * 255.0f);
            uint packedId = selectionBytes.x | (selectionBytes.y << 8);

            [branch] if (packedId == (uint)selectionId)
            {
                float coverage = coverageTexture.Load(sampleAddress).r;

                if (coverageThreshold < coverage)
                {
                    opacity = LegacyMultiply(coverageOpacity, opacity);
                }
            }

            return float4(HalfRtz(color.r), HalfRtz(color.g), HalfRtz(color.b), HalfRtz(opacity));
        }
        """;

    public static bool TryClassify(string vertex, string pixel, out IndexedUiVariant variant)
    {
        if (vertex == VertexHash && pixel == PixelHash)
        {
            variant = IndexedUiVariant.Dithered;

            return true;
        }

        if (vertex == PlainVertexHash && pixel == PlainPixelHash)
        {
            variant = IndexedUiVariant.Plain;

            return true;
        }

        if (vertex == DualAlphaVertexHash && pixel == DualAlphaPixelHash)
        {
            variant = IndexedUiVariant.DualAlphaMask;

            return true;
        }

        if (vertex == BatchedMaskVertexHash && pixel == BatchedMaskPixelHash)
        {
            variant = IndexedUiVariant.BatchedMask;

            return true;
        }

        if (vertex == TransformedTextureVertexHash && pixel == TransformedTexturePixelHash)
        {
            variant = IndexedUiVariant.TransformedTexture;

            return true;
        }

        if (vertex == BatchedDualTextureVertexHash && pixel == BatchedDualTexturePixelHash)
        {
            variant = IndexedUiVariant.BatchedDualTexture;

            return true;
        }

        variant = IndexedUiVariant.AlphaMask;

        return vertex == AlphaVertexHash && pixel == AlphaPixelHash;
    }

    public static uint VertexStride(IndexedUiVariant variant)
    {
        return variant switch
        {
            IndexedUiVariant.AlphaMask or IndexedUiVariant.DualAlphaMask => 20,
            IndexedUiVariant.BatchedMask or IndexedUiVariant.BatchedDualTexture => 16,
            _ => 12
        };
    }

    public static int VertexConstantBytes(IndexedUiVariant variant)
    {
        return variant switch
        {
            IndexedUiVariant.AlphaMask or IndexedUiVariant.TransformedTexture => 64,
            IndexedUiVariant.BatchedDualTexture => 64,
            IndexedUiVariant.BatchedMask => BatchedMaskConstantBytes,
            _ => 96
        };
    }

    public static int TextureCount(IndexedUiVariant variant)
    {
        return variant is IndexedUiVariant.Dithered or IndexedUiVariant.DualAlphaMask or
            IndexedUiVariant.BatchedDualTexture ? 2 : 1;
    }

    public static int BatchConstantBytes(IndexedUiVariant variant)
    {
        return variant is IndexedUiVariant.BatchedMask or IndexedUiVariant.BatchedDualTexture
            ? VertexConstantBytes(variant)
            : 0;
    }

    public static int PixelConstantBytes(IndexedUiVariant variant)
    {
        return variant switch
        {
            IndexedUiVariant.Dithered => 16,
            IndexedUiVariant.BatchedDualTexture => 32,
            _ => 0
        };
    }
}
