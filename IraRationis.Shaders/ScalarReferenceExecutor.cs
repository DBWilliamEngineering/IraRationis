// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders;

/// <summary>CPU reference semantics for the existing bounded scalar IR, not a wave emulator.</summary>
public static class ScalarReferenceExecutor
{
    public const int RegisterCount = 102;
    public const int StateWords = RegisterCount + 1;
    public const int MaximumCodeBytes = 4096;

    public static uint[] Execute(ReadOnlySpan<byte> code, ReadOnlySpan<uint> initial)
    {
        if (code.Length > MaximumCodeBytes || initial.Length != StateWords || initial[RegisterCount] > 1)
        {
            throw new ArgumentException("Invalid bounded scalar program or initial state.");
        }

        var instructions = Rdna2ScalarCompiler.Decode(code);
        var registers = initial.ToArray();

        foreach (var instruction in instructions)
        {
            if (instruction.Operation == ScalarOperation.End)
            {
                return registers;
            }

            if (instruction.Operation == ScalarOperation.Nop)
            {
                continue;
            }

            var left = Resolve(instruction.Left, registers);

            if (instruction.Operation == ScalarOperation.Move)
            {
                registers[instruction.Destination] = left;

                continue;
            }

            var right = Resolve(instruction.Right, registers);
            var result = instruction.Operation switch
            {
                ScalarOperation.Add => unchecked(left + right),
                ScalarOperation.Subtract => unchecked(left - right),
                ScalarOperation.And => left & right,
                ScalarOperation.Or => left | right,
                ScalarOperation.Xor => left ^ right,
                _ => throw new InvalidDataException("Unsupported scalar reference operation.")
            };
            var condition = instruction.Operation switch
            {
                ScalarOperation.Add => result < left,
                ScalarOperation.Subtract => left < right,
                _ => result != 0
            };

            registers[instruction.Destination] = result;
            registers[RegisterCount] = condition ? 1U : 0U;
        }

        throw new InvalidDataException("Scalar IR did not terminate.");
    }

    private static uint Resolve(ScalarOperand operand, ReadOnlySpan<uint> registers)
    {
        return operand.IsRegister ? registers[checked((int)operand.Value)] : operand.Value;
    }
}
