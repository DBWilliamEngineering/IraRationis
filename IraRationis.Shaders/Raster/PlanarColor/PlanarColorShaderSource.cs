// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Raster.PlanarColor;

internal static class PlanarColorShaderSource
{
    private const string Varyings = """
                                    struct PlanarVaryings
                                    {
                                        float4 Position : SV_Position;
                                        float4 Color : COLOR0;
                                        float4 Coordinates : TEXCOORD0;
                                    };
                                    """;

    private const string Arithmetic = """
                                      float LegacyMultiply(float left, float right)
                                      {
                                          return left == 0.0f || right == 0.0f ? 0.0f : left * right;
                                      }

                                      float3 LegacyMultiply3(float3 left, float3 right)
                                      {
                                          return float3(LegacyMultiply(left.x, right.x),
                                                        LegacyMultiply(left.y, right.y),
                                                        LegacyMultiply(left.z, right.z));
                                      }

                                      float4 LegacyMultiply4(float4 left, float right)
                                      {
                                          return float4(LegacyMultiply(left.x, right), LegacyMultiply(left.y, right),
                                                        LegacyMultiply(left.z, right), LegacyMultiply(left.w, right));
                                      }

                                      float HalfTowardZero(float value)
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

    private const string SharedConstants = """
                                           cbuffer SharedParameters : register(b2)
                                           {
                                               float4 SharedRows[9];
                                           };
                                           """;

    public static string Vertex
    {
        get
        {
            return Varyings + "\n" + Arithmetic + "\n" + SharedConstants + "\n" + """
                cbuffer SourceTransform : register(b0)
                {
                    float4 SourceRows[4];
                };

                cbuffer ClipTransform : register(b1)
                {
                    float4 ClipRows[4];
                };

                PlanarVaryings main(float3 position : POSITION0, float2 coordinates : TEXCOORD0, float4 color : COLOR0)
                {
                    precise float3 transformedPosition = mad(SourceRows[0].xyz, position.x, SourceRows[3].xyz);
                    transformedPosition = mad(SourceRows[1].xyz, position.y, transformedPosition);
                    transformedPosition = mad(SourceRows[2].xyz, position.z, transformedPosition);

                    precise float4 clipPosition = LegacyMultiply4(ClipRows[0], transformedPosition.x);
                    clipPosition = mad(ClipRows[1], transformedPosition.y, clipPosition);
                    clipPosition = mad(ClipRows[2], transformedPosition.z, clipPosition);
                    clipPosition = clipPosition + ClipRows[3];

                    PlanarVaryings result;
                    result.Position = clipPosition;
                    result.Color = color;
                    result.Coordinates = float4(LegacyMultiply(coordinates.x, SharedRows[6].x),
                                                LegacyMultiply(coordinates.y, SharedRows[6].y),
                                                LegacyMultiply(coordinates.x, SharedRows[6].z),
                                                LegacyMultiply(coordinates.y, SharedRows[6].w));
                    return result;
                }
                """;
        }
    }

    public static string Pixel
    {
        get
        {
            return Varyings + "\n" + Arithmetic + "\n" + SharedConstants + "\n" + """
                Texture2D<float4> FirstChromaPlane : register(t0);
                Texture2D<float4> LuminancePlane : register(t1);
                Texture2D<float4> SecondChromaPlane : register(t2);
                Texture3D<float4> ColorLookup : register(t3);
                SamplerState PlaneSampler : register(s0);

                float4 main(PlanarVaryings input) : SV_Target0
                {
                    float luminance = LuminancePlane.Sample(PlaneSampler, input.Coordinates.xy).r;
                    float firstChroma = FirstChromaPlane.Sample(PlaneSampler, input.Coordinates.zw).r;
                    float secondChroma = SecondChromaPlane.Sample(PlaneSampler, input.Coordinates.zw).r;
                    luminance = max(SharedRows[8].x, luminance);

                    precise float3 convertedColor = LegacyMultiply3(SharedRows[3].xyz, luminance.xxx);
                    convertedColor = mad(SharedRows[0].xyz, firstChroma, convertedColor);
                    convertedColor = mad(SharedRows[1].xyz, secondChroma, convertedColor);
                    convertedColor = convertedColor + SharedRows[2].xyz;
                    convertedColor = LegacyMultiply3(convertedColor, input.Color.rgb);

                    if (asuint(SharedRows[7].x) != 0u)
                    {
                        convertedColor = ColorLookup.SampleLevel(PlaneSampler, convertedColor, 0.0f).rgb;
                    }

                    return float4(HalfTowardZero(convertedColor.r), HalfTowardZero(convertedColor.g),
                                  HalfTowardZero(convertedColor.b), HalfTowardZero(input.Color.a));
                }
                """;
        }
    }
}
