// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Text;
using IraRationis.Shaders.Raster.Models;

namespace IraRationis.Shaders.Raster;

/// <summary>
/// Bounded, independently authored float4 graphics IR. Stage interfaces are fixed;
/// the expressions producing position, color, UV and target color are programmable.
/// This is not a console shader decoder.
/// </summary>
public sealed class ProgrammableRasterProgram
{
    private const int MaximumNodes = 128;
    private readonly RasterNode[] _vertex;
    private readonly RasterNode[] _pixel;
    private readonly int _position, _color, _uv, _target;

    public ProgrammableRasterProgram(ReadOnlySpan<RasterNode> vertex, int position, int color, int textureCoordinateExpression,
        ReadOnlySpan<RasterNode> pixel, int target)
    {
        _vertex = Validate(vertex, false);
        _pixel = Validate(pixel, true);
        _position = ValidateOutput(position, _vertex.Length);
        _color = ValidateOutput(color, _vertex.Length);
        _uv = ValidateOutput(textureCoordinateExpression, _vertex.Length);
        _target = ValidateOutput(target, _pixel.Length);
    }

    private static int ValidateOutput(int output, int count)
    {
        if ((uint)output >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(output));
        }

        return output;
    }

    private static RasterNode[] Validate(ReadOnlySpan<RasterNode> nodes, bool pixel)
    {
        if (nodes.IsEmpty || nodes.Length > MaximumNodes)
        {
            throw new ArgumentException("Invalid graphics expression count.", nameof(nodes));
        }

        var snapshot = nodes.ToArray();

        for (var index = 0; index < snapshot.Length; index++)
        {
            var node = snapshot[index];

            switch (node.Operation)
            {
                case RasterExpression.Position:
                case RasterExpression.Color:
                case RasterExpression.TexCoord:
                    break;
                case RasterExpression.Constant:
                    if (!Finite(node.Constant))
                    {
                        throw new ArgumentException("Nonfinite graphics constant.", nameof(nodes));
                    }

                    break;
                case RasterExpression.Add:
                case RasterExpression.Subtract:
                case RasterExpression.Multiply:
                    ValidateOutput(node.Left, index);
                    ValidateOutput(node.Right, index);

                    break;
                case RasterExpression.Saturate:
                    ValidateOutput(node.Left, index);

                    break;
                case RasterExpression.Sample when pixel:
                    ValidateOutput(node.Left, index);

                    break;
                default:
                    throw new NotSupportedException("Unsupported graphics expression or shader stage.");
            }
        }

        return snapshot;
    }

    public static bool Finite(Vector4 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }

    private const string Varyings = "struct Varyings { float4 Position : SV_Position; float4 Color : COLOR0; float4 UV : TEXCOORD0; };\n";

    public string EmitVertex()
    {
        var source = new StringBuilder(Varyings);

        source.AppendLine("struct Vertex { float4 Position : POSITION0; float4 Color : COLOR0; float4 UV : TEXCOORD0; };");
        source.AppendLine("Varyings main(Vertex input)\n{");

        EmitNodes(source, _vertex);

        source.AppendLine("    Varyings result;");
        source.AppendLine($"    result.Position = v{_position};");
        source.AppendLine($"    result.Color = v{_color};");
        source.AppendLine($"    result.UV = v{_uv};");
        source.AppendLine("    return result;\n}");

        return source.ToString();
    }

    public string EmitPixel()
    {
        var source = new StringBuilder(Varyings);

        source.AppendLine("Texture2D<float4> Image : register(t0);");
        source.AppendLine("SamplerState ImageSampler : register(s0);");
        source.AppendLine("float4 main(Varyings input) : SV_Target0\n{");

        EmitNodes(source, _pixel);

        source.AppendLine($"    return v{_target};\n}}");

        return source.ToString();
    }

    private static void EmitNodes(StringBuilder source, ReadOnlySpan<RasterNode> nodes)
    {
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var expression = node.Operation switch
            {
                RasterExpression.Position => "input.Position",
                RasterExpression.Color => "input.Color",
                RasterExpression.TexCoord => "input.UV",
                RasterExpression.Constant => ConstantExpression(node.Constant),
                RasterExpression.Add => $"v{node.Left} + v{node.Right}",
                RasterExpression.Subtract => $"v{node.Left} - v{node.Right}",
                RasterExpression.Multiply => $"v{node.Left} * v{node.Right}",
                RasterExpression.Saturate => $"saturate(v{node.Left})",
                RasterExpression.Sample => $"Image.Sample(ImageSampler, v{node.Left}.xy)",
                _ => throw new InvalidOperationException("Invalid graphics expression.")
            };

            source.AppendLine($"    precise float4 v{index} = {expression};");
        }
    }

    private static string ConstantExpression(Vector4 value)
    {
        return $"asfloat(uint4(0x{BitConverter.SingleToUInt32Bits(value.X):X8}u," +
               $"0x{BitConverter.SingleToUInt32Bits(value.Y):X8}u," +
               $"0x{BitConverter.SingleToUInt32Bits(value.Z):X8}u," +
               $"0x{BitConverter.SingleToUInt32Bits(value.W):X8}u))";
    }
}
