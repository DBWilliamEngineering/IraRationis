// SPDX-License-Identifier: GPL-3.0-or-later
// GFX10 scalar opcodes referenced from DexNom/VIctory victory-shader/src/gfx10/opcodes.rs.

using System.Buffers.Binary;
using System.Text;

namespace IraRationis.Shaders;

public enum ScalarOperation
{
    Nop,
    End,
    Move,
    Add,
    Subtract,
    And,
    Or,
    Xor
}

public readonly record struct ScalarOperand(bool IsRegister, uint Value);

public readonly record struct ScalarInstruction(
    int Offset,
    ScalarOperation Operation,
    int Destination,
    ScalarOperand Left,
    ScalarOperand Right);

// A deliberately strict first compiler slice. Unsupported instructions are refused,
// never treated as NOPs. No shader stage/resource/wave semantics are implied.
public static class Rdna2ScalarCompiler
{
    public static IReadOnlyList<ScalarInstruction> Decode(ReadOnlySpan<byte> code)
    {
        if (code.IsEmpty || (code.Length & 3) != 0)
        {
            throw new InvalidDataException("Invalid RDNA2 instruction stream length.");
        }

        var result = new List<ScalarInstruction>();
        var at = 0;

        while (at < code.Length)
        {
            var offset = at;
            var word = Next(code, ref at);
            var destination = (int)((word >> 16) & 0x7f);

            if ((word & 0xff800000) == 0xbf800000)
            {
                var op = (word >> 16) & 0x7f;

                switch (op)
                {
                    case 1:
                        result.Add(new ScalarInstruction(offset, ScalarOperation.End, 0, default, default));

                        return result; // unreachable trailing words are not decoded.
                    case 0:
                        result.Add(new ScalarInstruction(offset, ScalarOperation.Nop, 0, default, default));

                        continue;

                    default:
                        throw Unsupported(offset, word);
                }
            }

            if (destination > 101)
            {
                throw Unsupported(offset, word);
            }

            if ((word & 0xff800000) == 0xbe800000 && ((word >> 8) & 0xff) == 3)
            {
                var source = word & 0xff;
                var literal = source == 255 ? Next(code, ref at) : 0;

                result.Add(new ScalarInstruction(offset, ScalarOperation.Move, destination,
                    Operand(source, literal, offset), default));
            }
            else if ((word & 0xf0000000) == 0xb0000000 && ((word >> 23) & 0x1f) == 0)
            {
                result.Add(new ScalarInstruction(offset, ScalarOperation.Move, destination,
                    new ScalarOperand(false, unchecked((uint)(short)word)), default));
            }
            else if ((word & 0xc0000000) == 0x80000000 && (word & 0xf0000000) != 0xb0000000)
            {
                var op = ((word >> 23) & 0x7f) switch
                {
                    0 => ScalarOperation.Add,
                    1 => ScalarOperation.Subtract,
                    0x0e => ScalarOperation.And,
                    0x10 => ScalarOperation.Or,
                    0x12 => ScalarOperation.Xor,
                    _ => throw Unsupported(offset, word)
                };

                var left = word & 0xff;
                var right = (word >> 8) & 0xff;
                var literal = left == 255 || right == 255 ? Next(code, ref at) : 0;

                result.Add(new ScalarInstruction(offset, op, destination, Operand(left, literal, offset),
                    Operand(right, literal, offset)));
            }
            else
            {
                throw Unsupported(offset, word);
            }
        }

        throw new InvalidDataException("RDNA2 scalar stream has no endpgm.");
    }

    private static uint Next(ReadOnlySpan<byte> code, ref int at)
    {
        if (at > code.Length - 4)
        {
            throw new InvalidDataException($"Truncated RDNA2 literal at 0x{at:X}.");
        }

        var word = BinaryPrimitives.ReadUInt32LittleEndian(code.Slice(at, 4));

        at += 4;

        return word;
    }

    private static ScalarOperand Operand(uint raw, uint literal, int offset)
    {
        return raw switch
        {
            <= 101 => new ScalarOperand(true, raw),
            >= 128 and <= 192 => new ScalarOperand(false, raw - 128),
            >= 193 and <= 208 => new ScalarOperand(false, unchecked(0U - (raw - 192))),
            255 => new ScalarOperand(false, literal),
            _ => throw new NotSupportedException($"RDNA2 scalar operand {raw} at 0x{offset:X} is unsupported.")
        };
    }

    private static NotSupportedException Unsupported(int offset, uint word)
    {
        return new NotSupportedException($"RDNA2 instruction 0x{word:X8} at 0x{offset:X} is unsupported.");
    }

    private static string Expression(ScalarOperand operand)
    {
        return operand.IsRegister ? $"s[{operand.Value}]" : $"0x{operand.Value:X8}u";
    }

    // Diagnostic HLSL: one thread, initialized scalar registers, output registers + SCC.
    // This is a differential-test kernel, NOT a host replacement for a guest shader.
    public static string LowerDiagnostic(ReadOnlySpan<byte> code)
    {
        var instructions = Decode(code);
        var source = new StringBuilder(
            $"StructuredBuffer<uint> Input : register(t0);\n"
            + $"RWStructuredBuffer<uint> Output : register(u0);\n"
            + $"[numthreads(1,1,1)]\n"
            + $"void main()\n"
            + $"{{"
            + $"\n    "
            + $"uint s[102];\n    "
            + $"[unroll] for (uint i = 0; i < 102; ++i) {{ s[i] = Input[i]; }}\n    "
            + $"uint scc = Input[102];\n");

        foreach (var instruction in instructions)
        {
            if (instruction.Operation is ScalarOperation.Nop or ScalarOperation.End)
            {
                continue;
            }

            var a = Expression(instruction.Left);
            var b = Expression(instruction.Right);
            var d = instruction.Destination;

            if (instruction.Operation == ScalarOperation.Move)
            {
                source.AppendLine($"    s[{d}] = {a};");

                continue;
            }

            var op = instruction.Operation switch
            {
                ScalarOperation.Add => "+",
                ScalarOperation.Subtract => "-",
                ScalarOperation.And => "&",
                ScalarOperation.Or => "|",
                ScalarOperation.Xor => "^",
                ScalarOperation.Nop => "nop",      // Or ";" / "" (empty string) if you omit no-ops in output
                ScalarOperation.End => "return",   // Or "exit" / "end" to represent shader termination
                ScalarOperation.Move => "=",
                _ => throw new InvalidOperationException("Unsupported scalar IR.")
            };

            source.AppendLine($"    {{ uint a = {a}; uint b = {b}; uint r = a {op} b;");

            var condition = instruction.Operation switch
            {
                ScalarOperation.Add => "r < a",
                ScalarOperation.Subtract => "a < b",
                _ => "r != 0"
            };

            source.AppendLine($"      scc = ({condition}) ? 1u : 0u; s[{d}] = r; }}");
        }

        source.AppendLine(
            "    [unroll] for (uint outputIndex = 0; outputIndex < 102; ++outputIndex) { Output[outputIndex] = s[outputIndex]; }\n    Output[102] = scc;\n}");

        return source.ToString();
    }
}
