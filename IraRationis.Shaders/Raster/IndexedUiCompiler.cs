// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Raster;

/// <summary>Bounded lowering of the captured indexed UI shader contract.</summary>
public enum IndexedUiVariant
{
    Dithered,
    Plain,
    AlphaMask
}

public static class IndexedUiCompiler
{
    public const string VertexHash = "1CC31822133D45A29549294825B8E3AA09EE1735D794E808F91C4A7A03985E85";
    public const string PixelHash = "0B02670BFF967D1208E013E5750134321D18BE90F905EDCAEC4C369E2BAD5ECF";
    public const string RootHash = "DF44DC5144517A00F01BE4238EC63F9463BFFC8A035C2E02E73CA5AE165D9355";

    public const string PlainVertexHash = "DA7A336AB58A455C0A64D573B51CD800F86D8E6AE53E5247315CF01B3FED6737";
    public const string PlainPixelHash = "F060DE150448E708BD7D71BE21A5654F8C04471DFFB529815FED7471EC745397";

    public const string AlphaVertexHash = "3894C7D633149F71CC98B6F25AFF33CBA31958AB2D3B1D6FC0C34244944AAEFA";
    public const string AlphaPixelHash = "605A48C19028F3C4440C22FDAF2C5084F563096EE8C88E1B7BF0C691AB586975";

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
        variant = IndexedUiVariant.AlphaMask;
        return vertex == AlphaVertexHash && pixel == AlphaPixelHash;
    }

    public static uint VertexStride(IndexedUiVariant variant)
    {
        return variant == IndexedUiVariant.AlphaMask ? 20U : 12U;
    }

    public static int VertexConstantBytes(IndexedUiVariant variant)
    {
        return variant == IndexedUiVariant.AlphaMask ? 64 : 96;
    }

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

}
