// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Raster.DualMask;

internal static class DualMaskRectangleShader
{
    internal const string PixelSource = """
        Texture2D<float4> FirstMask : register(t0);
        Texture2D<float4> SecondMask : register(t1);
        SamplerState PointWrap : register(s0);

        cbuffer GuestConstants : register(b27, space1)
        {
            float4 MaskCurve : packoffset(c3);
        };

        float4 main(Varyings input) : SV_Target0
        {
            float firstCoverage = FirstMask.SampleLevel(PointWrap, input.Texcoord, 0.0f).a;
            float secondCoverage = SecondMask.SampleLevel(PointWrap, input.Texcoord, 0.0f).a;
            const float logOfZero = asfloat(0xff800000u);
            precise float reciprocalFirstPower = rcp(MaskCurve.z);
            precise float reciprocalSecondPower = rcp(MaskCurve.w);
            precise float firstZero = exp2(LegacyMul(logOfZero, reciprocalFirstPower));
            precise float secondZero = exp2(LegacyMul(logOfZero, reciprocalSecondPower));
            precise float firstRemaining = 1.0f - firstZero;
            precise float secondRemaining = 1.0f - secondZero;
            precise float curveInput = max(0.0f, mad(firstZero, rcp(firstRemaining), MaskCurve.y));
            precise float firstNormalization = exp2(LegacyMul(MaskCurve.z, log2(firstRemaining)));
            precise float secondNormalization = rcp(exp2(LegacyMul(MaskCurve.w, log2(secondRemaining))));
            precise float poweredInput = exp2(LegacyMul(MaskCurve.z, log2(curveInput)));
            precise float firstNormalized = LegacyMul(poweredInput, firstNormalization);
            precise float curveBase = max(0.0f, LegacyMul(firstNormalized, secondNormalization));
            precise float curveOffset = mad(-secondZero, rcp(secondRemaining), -MaskCurve.x);
            precise float curveValue = exp2(LegacyMul(log2(curveBase), reciprocalSecondPower));
            precise float curveDelta = curveOffset + curveValue;
            precise float maskedCurve = mad(firstCoverage, curveDelta, MaskCurve.x);
            precise float grayscale = mad(-secondCoverage, maskedCurve, maskedCurve);
            float exported = HalfRtz(grayscale);

            return float4(exported, exported, exported, 1.0f);
        }
        """;
}
