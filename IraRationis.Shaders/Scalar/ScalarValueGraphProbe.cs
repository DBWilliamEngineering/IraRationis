// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace IraRationis.Shaders.Scalar;

public static class ScalarValueGraphProbe
{
    public static void Verify()
    {
        const int programs = 64;
        const int operations = 48;
        const int cases = 8;
        var random = new Random(65);
        var code = new byte[(operations + 1) * sizeof(uint)];
        var initial = new uint[ScalarReferenceExecutor.StateWords];
        uint[] opcodes = [0, 1, 0x0e, 0x10, 0x12];
        var removed = 0;

        for (var program = 0; program < programs; program++)
        {
            for (var index = 0; index < operations; index++)
            {
                var destination = (uint)random.Next(0, 8);
                var left = (uint)(random.Next(2) == 0 ? random.Next(0, 8) : random.Next(128, 209));
                var right = (uint)(random.Next(2) == 0 ? random.Next(0, 8) : random.Next(128, 209));
                var opcode = opcodes[random.Next(opcodes.Length)];
                var word = 0x80000000U | (opcode << 23) | (destination << 16) | (right << 8) | left;

                if (index % 7 == 0)
                {
                    word = 0xbe800300U | (destination << 16) | left;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(index * sizeof(uint)), word);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(operations * sizeof(uint)), 0xbf810000U);

            var graph = ScalarValueGraph.Build(code);

            removed += graph.ValueCount - graph.LiveValueCount;

            for (var sample = 0; sample < cases; sample++)
            {
                random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(initial.AsSpan()));

                initial[ScalarReferenceExecutor.RegisterCount] &= 1;

                if (sample == 0)
                {
                    initial[0] = uint.MaxValue;
                    initial[1] = 1;
                }

                var expected = ScalarReferenceExecutor.Execute(code, initial);
                var actual = graph.Evaluate(initial);

                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    throw new InvalidOperationException($"R65 scalar graph mismatch: program={program}, sample={sample}.");
                }
            }
        }

        if (removed == 0)
        {
            throw new InvalidOperationException("R65 scalar graph failed to remove dead values.");
        }

        // Verify that carry and borrow remain observable even when the arithmetic
        // destination aliases an input. A subsequent move must preserve SCC.
        uint[] edgeWords = [0x80000100, 0xbe800381, 0xbf810000];
        var edgeCode = new byte[edgeWords.Length * sizeof(uint)];

        for (var index = 0; index < edgeWords.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(edgeCode.AsSpan(index * sizeof(uint)), edgeWords[index]);
        }

        Array.Clear(initial);

        initial[0] = uint.MaxValue;
        initial[1] = 1;

        var edge = ScalarValueGraph.Build(edgeCode).Evaluate(initial);

        if (edge[0] != 1 || edge[ScalarReferenceExecutor.RegisterCount] != 1)
        {
            throw new InvalidOperationException("R65 scalar graph carry was lost across an aliased overwrite.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(edgeCode, 0x80800100U);

        initial[0] = 0;

        edge = ScalarValueGraph.Build(edgeCode).Evaluate(initial);

        if (edge[0] != 1 || edge[ScalarReferenceExecutor.RegisterCount] != 1)
        {
            throw new InvalidOperationException("R65 scalar graph borrow was lost across an aliased overwrite.");
        }

        Console.WriteLine($"[runtime] IRX-R65-CPU: programs={programs}; states={programs * cases}; deadValues={removed}; carryBorrow=verified; reference=verified.");
    }
}
