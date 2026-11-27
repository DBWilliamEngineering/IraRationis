// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Shaders.Compute.Models;

namespace IraRationis.Shaders.Compute;

/// <summary>Original, title-independent compute fixture and independent expected results.</summary>
public static class TypedComputeProbe
{
    private const int Budget = 32;

    public static TypedComputeProgram CreateProgram()
    {
        ComputeValueKind[] registers =
        [
            ComputeValueKind.UInt32, ComputeValueKind.UInt32, ComputeValueKind.UInt32,
            ComputeValueKind.UInt32, ComputeValueKind.UInt32, ComputeValueKind.Float32,
            ComputeValueKind.Float32, ComputeValueKind.Float32, ComputeValueKind.UInt32,
            ComputeValueKind.UInt32, ComputeValueKind.UInt32, ComputeValueKind.UInt32
        ];
        ComputeBlock[] blocks =
        [
            new ComputeBlock(
            [
                new ComputeInstruction(ComputeOperation.Constant, 1, Literal: new ComputeVector(1, 2, 3, 4)),
                new ComputeInstruction(ComputeOperation.Constant, 6, Literal: FloatVector(2, 2, 0.5f, 4)),
                new ComputeInstruction(ComputeOperation.Constant, 10, Literal: new ComputeVector(255, 255, 255, 255))
            ], new ComputeTerminator(ComputeTransfer.Jump, 1)),
            new ComputeBlock(
            [
                new ComputeInstruction(ComputeOperation.LessThan, 3, 0, 2)
            ], new ComputeTerminator(ComputeTransfer.Branch, 2, 3, 3)),
            new ComputeBlock(
            [
                new ComputeInstruction(ComputeOperation.Add, 0, 0, 1)
            ], new ComputeTerminator(ComputeTransfer.Jump, 1)),
            new ComputeBlock(
            [
                new ComputeInstruction(ComputeOperation.Multiply, 7, 5, 6),
                new ComputeInstruction(ComputeOperation.Add, 7, 7, 5),
                new ComputeInstruction(ComputeOperation.Subtract, 7, 7, 5),
                new ComputeInstruction(ComputeOperation.LessThan, 3, 5, 6),
                new ComputeInstruction(ComputeOperation.Select, 7, 7, 5, 3),
                new ComputeInstruction(ComputeOperation.Swizzle, 7, 7, Literal: new ComputeVector(3, 2, 1, 0)),
                new ComputeInstruction(ComputeOperation.Bitcast, 8, 7),
                new ComputeInstruction(ComputeOperation.Bitcast, 7, 8),
                new ComputeInstruction(ComputeOperation.Multiply, 9, 0, 1),
                new ComputeInstruction(ComputeOperation.Subtract, 9, 9, 1),
                new ComputeInstruction(ComputeOperation.And, 9, 9, 10),
                new ComputeInstruction(ComputeOperation.Constant, 4, Literal: new ComputeVector(256, 256, 256, 256)),
                new ComputeInstruction(ComputeOperation.Or, 9, 9, 4),
                new ComputeInstruction(ComputeOperation.Xor, 9, 9, 4),
                new ComputeInstruction(ComputeOperation.Equal, 11, 8, 8),
                new ComputeInstruction(ComputeOperation.Copy, 4, 11)
            ], new ComputeTerminator(ComputeTransfer.Return))
        ];

        return new TypedComputeProgram(registers, blocks, [2, 5], [0, 7, 8, 9, 3, 4], Budget);
    }

    public static uint[] CreateInput(uint iterations)
    {
        var input = new uint[TypedComputeProgram.StateWords];

        input[0] = iterations;
        input[1] = iterations;
        input[2] = iterations;
        input[3] = iterations;

        WriteVector(input, 4, FloatVector(1, 4, 0.25f, 2));

        return input;
    }

    public static uint[] Expected(uint iterations)
    {
        var output = new uint[TypedComputeProgram.StateWords];

        // Entry + two blocks per iteration + final condition + return.
        if (iterations > (Budget - 3) / 2)
        {
            return output;
        }

        var floats = FloatVector(8, 0.125f, 4, 2);

        WriteVector(output, 0, new ComputeVector(iterations, 2 * iterations, 3 * iterations, 4 * iterations));
        WriteVector(output, 4, floats);
        WriteVector(output, 8, floats);
        WriteVector(output, 12, new ComputeVector(unchecked(iterations - 1) & 255,
            unchecked(4 * iterations - 2) & 255, unchecked(9 * iterations - 3) & 255,
            unchecked(16 * iterations - 4) & 255));
        WriteVector(output, 16, new ComputeVector(1, 0, 1, 1));
        WriteVector(output, 20, new ComputeVector(1, 1, 1, 1));

        output[TypedComputeProgram.StatusWord] = 1;

        return output;
    }

    public static void Verify(TypedComputeProgram program)
    {
        for (var index = 0u; index < 64; index++)
        {
            var iterations = index == 63 ? uint.MaxValue : index;
            var expected = Expected(iterations);

            if (!program.Evaluate(CreateInput(iterations)).AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException($"Typed compute CPU mismatch at iteration count {iterations}.");
            }
        }

        ComputeValueKind[] kinds = [ComputeValueKind.UInt32, ComputeValueKind.Float32];
        var end = new ComputeTerminator(ComputeTransfer.Return);
        var valid = new ComputeBlock([], end);

        Reject(() => new TypedComputeProgram(kinds, [new ComputeBlock([], new ComputeTerminator(ComputeTransfer.Jump, 1))], [], [], 1));
        Reject(() => new TypedComputeProgram(kinds, [valid, valid], [], [], 1));
        Reject(() => new TypedComputeProgram(kinds, [valid], [0, 0], [], 1));
        Reject(() => new TypedComputeProgram(kinds, [valid], [], [2], 1));
        Reject(() => new TypedComputeProgram(kinds, [valid], [], [], 0));
        Reject(() => new TypedComputeProgram(kinds,
            [new ComputeBlock([new ComputeInstruction(ComputeOperation.Add, 0, 0, 1)], end)], [], [], 1));
        Reject(() => new TypedComputeProgram(kinds,
            [new ComputeBlock([new ComputeInstruction(ComputeOperation.Swizzle, 0, 0, Literal: new ComputeVector(0, 1, 2, 4))], end)], [], [], 1));
        Reject(() => new TypedComputeProgram(kinds,
            [new ComputeBlock([], new ComputeTerminator(ComputeTransfer.Branch, 1, 1, 1)), valid], [], [], 1));
        Reject(() => program.Evaluate(new uint[TypedComputeProgram.StateWords - 1]));

        Console.WriteLine("[runtime] IRX-R66-CPU: states=64; blocks=4; vectorTypes=2; invalidCases=9; loopBudget=verified; branch=verified; reference=verified.");
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid typed compute program or input was accepted.");
    }

    private static ComputeVector FloatVector(float coordinateX, float coordinateY, float coordinateZ, float coordinateW)
    {
        return new ComputeVector(BitConverter.SingleToUInt32Bits(coordinateX), BitConverter.SingleToUInt32Bits(coordinateY),
            BitConverter.SingleToUInt32Bits(coordinateZ), BitConverter.SingleToUInt32Bits(coordinateW));
    }

    private static void WriteVector(Span<uint> output, int offset, ComputeVector value)
    {
        output[offset] = value.X;
        output[offset + 1] = value.Y;
        output[offset + 2] = value.Z;
        output[offset + 3] = value.W;
    }
}
