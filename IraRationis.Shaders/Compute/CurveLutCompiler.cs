// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Native.Models;

namespace IraRationis.Shaders.Compute;

/// <summary>Instruction-derived bounded piecewise cubic curve LUT. Not bit-exact RDNA arithmetic.</summary>
public static class CurveLutCompiler
{
    public const string ContainerHash = "1DFFAC488A540AD1AF4AB074194C5938ED402139B11D23DF0FDE47A50AF902C8";
    private const string ProgramHash = "18CBDFAF2F2C67DE6FE202793AFF1EEB8A5F6EBF55E47A98CE3880E544478B8B";
    public const string ThreeSegmentContainerHash = "453A784E21B288DEF506EECA289F7DA47582CAD190E12C29452314FA476011A1";
    private const string ThreeSegmentProgramHash = "7D8BEA6B089B89D22A28FB553B01F29E7D40F1F1F6A65CE5A4AA4D144132C84E";
    public const int SegmentBytes = 32;
    public const int CurveBytes = 64;
    public const int MaximumCurveBytes = 96;
    public const int ParameterBytes = 16;
    public const uint ThreadsPerGroup = 128;

    // Constant layout: float step, uint count, float divisor, float offset.
    // Curve layout: four X and four Y control points per segment.
    // Preserve the native root priority for the quadratic and cubic cases. The
    // host transcendental/rounding behavior may differ from RDNA2 near degeneracy.
    private const string Source = """
                                  RWTexture1D<$TYPE$> Output : register(u0);
                                  cbuffer Parameters : register(b0) { float Step; uint Count; float Divisor; float Offset; };
                                  cbuffer Curves : register(b1) { float4 X0; float4 Y0; float4 X1; float4 Y1; $FIELDS$ };
                                  bool Unit(float t) { return t >= 0.0f && t <= 1.0f; }
                                  float CubeRoot(float x)
                                  {
                                      if (x == 0.0f) { return 0.0f; }
                                      float magnitude = pow(abs(x), 1.0f / 3.0f);
                                      return x < 0.0f ? -magnitude : magnitude;
                                  }
                                  float Bezier(float4 p, float t)
                                  {
                                      float s = 1.0f - t;
                                      return s * s * (p.x * s + 3.0f * p.y * t) + t * t * (3.0f * p.z * s + p.w * t);
                                  }
                                  float Parameter(float4 p, float x)
                                  {
                                      if (x == p.x) { return 0.0f; }
                                      if (x == p.w) { return 1.0f; }
                                      float a = p.w - p.x + 3.0f * (p.y - p.z);
                                      float b = 3.0f * (p.x - 2.0f * p.y + p.z);
                                      float c = 3.0f * (p.y - p.x);
                                      float d = p.x - x;
                                      const float epsilon = 0.00001f;
                                      if (abs(a) < epsilon)
                                      {
                                          if (abs(b) < epsilon) { return abs(c) < epsilon ? -1.0f : -d / c; }
                                          float discriminant = c * c - 4.0f * b * d;
                                          if (discriminant < 0.0f) { return -1.0f; }
                                          float q = sqrt(discriminant);
                                          float first = (q - c) / (2.0f * b);
                                          float second = (-c - q) / (2.0f * b);
                                          return Unit(first) ? first : (Unit(second) ? second : -1.0f);
                                      }
                                      if (abs(d) < epsilon) { return 0.0f; }
                                      float B = b / a;
                                      float C = c / a;
                                      float D = d / a;
                                      float p3 = (3.0f * C - B * B) / 9.0f;
                                      float q2 = (-27.0f * D + B * (9.0f * C - 2.0f * B * B)) / 54.0f;
                                      float disc = p3 * p3 * p3 + q2 * q2;
                                      float shift = B / 3.0f;
                                      if (disc > 0.0f)
                                      {
                                          // Avoid cancellation in the smaller Cardano radicand. The
                                          // two cube roots have product -p3. This also avoids taking
                                          // the cube root of a spurious near-zero residual when p3=0.
                                          if (p3 == 0.0f) { return CubeRoot(2.0f * q2) - shift; }
                                          float h = sqrt(disc);
                                          float u = CubeRoot(q2 >= 0.0f ? q2 + h : q2 - h);
                                          if (u == 0.0f) { return -shift; }
                                          return u - p3 / u - shift;
                                      }
                                      if (disc == 0.0f)
                                      {
                                          float u = CubeRoot(q2);
                                          float first = 2.0f * u - shift;
                                          return Unit(first) ? first : -u - shift;
                                      }
                                      float angle = acos(clamp(q2 / sqrt(-p3 * p3 * p3), -1.0f, 1.0f));
                                      float radius = 2.0f * sqrt(-p3);
                                      float firstRoot = radius * cos(angle / 3.0f) - shift;
                                      if (Unit(firstRoot)) { return firstRoot; }
                                      float secondRoot = radius * cos((angle + 6.28318530718f) / 3.0f) - shift;
                                      if (Unit(secondRoot)) { return secondRoot; }
                                      return radius * cos((angle + 12.56637061436f) / 3.0f) - shift;
                                  }
                                  [numthreads(128, 1, 1)]
                                  void main(uint3 id : SV_DispatchThreadID)
                                  {
                                      uint width;
                                      Output.GetDimensions(width);
                                      if (id.x >= Count || id.x >= width) { return; }
                                      float x = mad(Step, (float)id.x, -Offset) / Divisor;
                                      float y = x < X0.w ? Bezier(Y0, Parameter(X0, x)) : $TAIL$;
                                      Output[id.x] = $VALUE$;
                                  }
                                  """;

    public static int SegmentCount(string hash)
    {
        return hash switch
        {
            ContainerHash => 2,
            ThreeSegmentContainerHash => 3,
            _ => 0
        };
    }

    public static string Translate(ReadOnlyMemory<byte> shader, uint format)
    {
        var segments = SegmentCount(Convert.ToHexString(SHA256.HashData(shader.Span)));

        if (segments == 0 || shader.Length != (segments == 2 ? 2012 : 2168))
        {
            throw new NotSupportedException("Unsupported curve LUT container.");
        }

        var programs = NativeProgramReader.Read(ShaderContainer.Parse(shader));

        if (programs is not
            [
                {
                    Stage: NativeShaderStage.Compute, Wave64: true, VectorRegisters: 7,
                    LocalBytes: 0, Threads: [128, 1, 1]
                } program
            ] ||
            program.ScalarRegisters != (segments == 2 ? 30 : 32) ||
            program.Code.Length != (segments == 2 ? 2368 : 3456) || program.RootLayout.Length != 900
            || program.UsedResources.Length != 60 ||
            Convert.ToHexString(SHA256.HashData(program.Code))
            != (segments == 2 ? ProgramHash : ThreeSegmentProgramHash) ||
            !MatchesBinding(program.UsedResources.AsSpan(0, 20), 0, 0, 71, 32, 4) ||
            !MatchesBinding(program.UsedResources.AsSpan(20, 20), 3, 8, 6, 4, 8) ||
            !MatchesBinding(program.UsedResources.AsSpan(40, 20), 2, 16, 0, 0, 4))
        {
            throw new NotSupportedException("Unsupported curve LUT native contract.");
        }

        return CreateHostSource(format, segments);
    }

    private static bool MatchesBinding(ReadOnlySpan<byte> entry, byte kind, uint offset, uint register, uint stride,
        byte dwords)
    {
        return entry[0] == kind && entry[1] == 3 && BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]) == offset &&
               BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]) == register &&
               BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]) == stride && entry[16] == dwords &&
               entry[17] == 5 && entry[18] == 1;
    }

    public static bool SupportsFormat(uint format)
    {
        return format is 2 or 10 or 11 or 13 or 16 or 24 or 28 or 31 or 34 or 35 or 37 or 41 or 49 or 51 or 54 or 56
            or 58 or 61 or 63;
    }

    public static string CreateHostSource(uint format, int segments = 2)
    {
        if (segments is not (2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(segments));
        }

        var type = format switch
        {
            41 or 54 => "float",
            56 or 61 => "unorm float",
            58 or 63 => "snorm float",
            16 or 34 => "float2",
            35 or 49 => "unorm float2",
            37 or 51 => "snorm float2",
            2 or 10 => "float4",
            11 or 24 or 28 => "unorm float4",
            13 or 31 => "snorm float4",
            _ => throw new NotSupportedException($"Unsupported curve UAV format {format}.")
        };
        var value = type.EndsWith('4') ? "float4(y, y, y, y)" : type.EndsWith('2') ? "float2(y, y)" : "y";

        var fields = segments == 3 ? "float4 X2; float4 Y2;" : string.Empty;
        var tail = segments == 3
            ? "(x < X1.w ? Bezier(Y1, Parameter(X1, x)) : Bezier(Y2, Parameter(X2, x)))"
            : "Bezier(Y1, Parameter(X1, x))";

        return Source.Replace("$TYPE$", type, StringComparison.Ordinal)
            .Replace("$VALUE$", value, StringComparison.Ordinal)
            .Replace("$FIELDS$", fields, StringComparison.Ordinal)
            .Replace("$TAIL$", tail, StringComparison.Ordinal);
    }
}
