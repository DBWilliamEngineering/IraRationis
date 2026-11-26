// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace IraRationis.Shaders;

public enum ComputeValueKind
{
    UInt32,
    Float32
}

public enum ComputeOperation
{
    Constant,
    Copy,
    Bitcast,
    Add,
    Subtract,
    Multiply,
    And,
    Or,
    Xor,
    LessThan,
    Equal,
    Select,
    Swizzle
}

public enum ComputeTransfer
{
    Return,
    Jump,
    Branch
}

/// <summary>Four raw 32-bit lanes; floating-point values preserve their bit representation.</summary>
public readonly record struct ComputeVector(uint X, uint Y, uint Z, uint W)
{
    public uint Lane(int index)
    {
        return index switch
        {
            0 => X,
            1 => Y,
            2 => Z,
            3 => W,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

public readonly record struct ComputeInstruction(ComputeOperation Operation, int Destination,
    int Left = -1, int Right = -1, int Mask = -1, ComputeVector Literal = default);

public readonly record struct ComputeTerminator(ComputeTransfer Transfer, int Target = -1,
    int Alternative = -1, int Condition = -1);

public sealed record ComputeBlock(IReadOnlyList<ComputeInstruction> Instructions, ComputeTerminator Terminator);

/// <summary>
///     An independently authored compute IR, not a decoder for console instructions.
///     Typed four-lane registers, explicit block edges, bounded execution and packed IO.
///     Branches inspect lane X of an unsigned mask; comparisons produce 0 or 1 per lane.
///     The final state word is 1 on return, 0 when the block budget is exhausted.
/// </summary>
public sealed class TypedComputeProgram
{
    public const int StateWords = ScalarReferenceExecutor.StateWords;
    public const int StatusWord = StateWords - 1;
    public const int MaximumRegisters = 24;
    public const int MaximumBlocks = 32;
    public const int MaximumInstructions = 256;
    public const int MaximumBlockBudget = 128;

    private const string LaneNames = "xyzw";

    private readonly ComputeValueKind[] _registers;
    private readonly ComputeBlock[] _blocks;
    private readonly int[] _inputs;
    private readonly int[] _outputs;
    private readonly int _budget;

    public int BlockCount
    {
        get
        {
            return _blocks.Length;
        }
    }

    public int RegisterCount
    {
        get
        {
            return _registers.Length;
        }
    }

    public int BlockBudget
    {
        get
        {
            return _budget;
        }
    }

    public int InstructionCount { get; }

    public TypedComputeProgram(IReadOnlyList<ComputeValueKind> registers, IReadOnlyList<ComputeBlock> blocks,
        IReadOnlyList<int> inputs, IReadOnlyList<int> outputs, int blockBudget)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);

        if (registers.Count is < 1 or > MaximumRegisters || blocks.Count is < 1 or > MaximumBlocks ||
            inputs.Count > registers.Count || outputs.Count > registers.Count ||
            blockBudget is < 1 or > MaximumBlockBudget)
        {
            throw new ArgumentException("Typed compute program exceeds its declared bounds.");
        }

        _registers = registers.ToArray();
        _inputs = inputs.ToArray();
        _outputs = outputs.ToArray();
        _budget = blockBudget;
        _blocks = new ComputeBlock[blocks.Count];

        if (_registers.Any(static kind => kind is not (ComputeValueKind.UInt32 or ComputeValueKind.Float32)))
        {
            throw new ArgumentException("Unknown compute register type.");
        }

        ValidateBindings(_inputs);
        ValidateBindings(_outputs);

        var count = 0;

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];

            ArgumentNullException.ThrowIfNull(block);
            ArgumentNullException.ThrowIfNull(block.Instructions);

            if (block.Instructions.Count > MaximumInstructions - count)
            {
                throw new ArgumentException("Typed compute instruction budget exceeded.");
            }

            var instructions = block.Instructions.ToArray();

            foreach (var instruction in instructions)
            {
                ValidateInstruction(instruction);
            }

            ValidateTerminator(block.Terminator);

            _blocks[index] = new ComputeBlock(instructions, block.Terminator);

            count += instructions.Length;
        }

        InstructionCount = count;

        ValidateReachability();
    }

    private ComputeValueKind Kind(int register)
    {
        if ((uint)register >= (uint)_registers.Length)
        {
            throw new ArgumentException("Compute register is out of range.");
        }

        return _registers[register];
    }

    private void ValidateBindings(int[] bindings)
    {
        var seen = new HashSet<int>();

        foreach (var register in bindings)
        {
            _ = Kind(register);

            if (!seen.Add(register))
            {
                throw new ArgumentException("Duplicate compute IO binding.");
            }
        }
    }

    private void ValidateInstruction(ComputeInstruction instruction)
    {
        var kind = Kind(instruction.Destination);

        switch (instruction.Operation)
        {
            case ComputeOperation.Constant:
                return;
            case ComputeOperation.Copy:
            case ComputeOperation.Swizzle:
                if (Kind(instruction.Left) != kind || (instruction.Operation == ComputeOperation.Swizzle &&
                    (instruction.Literal.X > 3 || instruction.Literal.Y > 3 ||
                     instruction.Literal.Z > 3 || instruction.Literal.W > 3)))
                {
                    throw new ArgumentException("Invalid copy or swizzle.");
                }

                return;
            case ComputeOperation.Bitcast:
                if (Kind(instruction.Left) == kind)
                {
                    throw new ArgumentException("Bitcast must change register type.");
                }

                return;
            case ComputeOperation.LessThan:
            case ComputeOperation.Equal:
                if (kind != ComputeValueKind.UInt32 || Kind(instruction.Left) != Kind(instruction.Right))
                {
                    throw new ArgumentException("Comparison requires matching operands and an unsigned mask.");
                }

                return;
            case ComputeOperation.Select:
                if (Kind(instruction.Mask) != ComputeValueKind.UInt32 || Kind(instruction.Left) != kind ||
                    Kind(instruction.Right) != kind)
                {
                    throw new ArgumentException("Select requires a mask and matching values.");
                }

                return;
            case ComputeOperation.And:
            case ComputeOperation.Or:
            case ComputeOperation.Xor:
                if (kind != ComputeValueKind.UInt32)
                {
                    throw new ArgumentException("Bitwise operation requires unsigned operands.");
                }

                break;
            case ComputeOperation.Add:
            case ComputeOperation.Subtract:
            case ComputeOperation.Multiply:
                break;
            default:
                throw new ArgumentException("Unknown compute operation.");
        }

        if (Kind(instruction.Left) != kind || Kind(instruction.Right) != kind)
        {
            throw new ArgumentException("Compute operand type mismatch.");
        }
    }

    private void ValidateTerminator(ComputeTerminator terminator)
    {
        if (terminator.Transfer == ComputeTransfer.Return)
        {
            return;
        }

        if ((uint)terminator.Target >= (uint)_blocks.Length)
        {
            throw new ArgumentException("Invalid compute block target.");
        }

        if (terminator.Transfer == ComputeTransfer.Jump)
        {
            return;
        }

        if (terminator.Transfer != ComputeTransfer.Branch || (uint)terminator.Alternative >= (uint)_blocks.Length ||
            Kind(terminator.Condition) != ComputeValueKind.UInt32)
        {
            throw new ArgumentException("Invalid compute branch.");
        }
    }

    private void ValidateReachability()
    {
        var seen = new bool[_blocks.Length];
        var pending = new Queue<int>();
        var returns = false;

        seen[0] = true;
        pending.Enqueue(0);

        void Visit(int target)
        {
            if (!seen[target])
            {
                seen[target] = true;
                pending.Enqueue(target);
            }
        }

        while (pending.TryDequeue(out var index))
        {
            var terminator = _blocks[index].Terminator;

            if (terminator.Transfer == ComputeTransfer.Return)
            {
                returns = true;
                continue;
            }

            Visit(terminator.Target);

            if (terminator.Transfer == ComputeTransfer.Branch)
            {
                Visit(terminator.Alternative);
            }
        }

        if (!returns || seen.Contains(false))
        {
            throw new ArgumentException("Compute graph requires a reachable return and no unreachable blocks.");
        }
    }

    /// <summary>CPU oracle. The supported floating-point validation domain is finite normal values and zeros.</summary>
    public uint[] Evaluate(ReadOnlySpan<uint> input)
    {
        if (input.Length != StateWords)
        {
            throw new ArgumentException("Invalid packed compute input extent.", nameof(input));
        }

        var values = new ComputeVector[_registers.Length];

        for (var index = 0; index < _inputs.Length; index++)
        {
            var start = index * 4;

            values[_inputs[index]] = new ComputeVector(input[start], input[start + 1], input[start + 2], input[start + 3]);
        }

        var current = 0;
        var returned = false;

        for (var step = 0; step < _budget && !returned; step++)
        {
            var block = _blocks[current];

            foreach (var instruction in block.Instructions)
            {
                values[instruction.Destination] = EvaluateInstruction(instruction, values);
            }

            var end = block.Terminator;

            switch (end.Transfer)
            {
                case ComputeTransfer.Return:
                    returned = true;
                    break;
                case ComputeTransfer.Jump:
                    current = end.Target;
                    break;
                case ComputeTransfer.Branch:
                    current = values[end.Condition].X != 0 ? end.Target : end.Alternative;
                    break;
            }
        }

        var result = new uint[StateWords];

        // A non-returning program must not publish partial outputs.
        if (returned)
        {
            for (var index = 0; index < _outputs.Length; index++)
            {
                var value = values[_outputs[index]];
                var start = index * 4;

                result[start] = value.X;
                result[start + 1] = value.Y;
                result[start + 2] = value.Z;
                result[start + 3] = value.W;
            }

            result[StatusWord] = 1;
        }

        return result;
    }

    private ComputeVector EvaluateInstruction(ComputeInstruction instruction, ComputeVector[] values)
    {
        if (instruction.Operation == ComputeOperation.Constant)
        {
            return instruction.Literal;
        }

        var left = values[instruction.Left];

        if (instruction.Operation is ComputeOperation.Copy or ComputeOperation.Bitcast)
        {
            return left;
        }

        if (instruction.Operation == ComputeOperation.Swizzle)
        {
            var lanes = instruction.Literal;

            return new ComputeVector(left.Lane((int)lanes.X), left.Lane((int)lanes.Y),
                left.Lane((int)lanes.Z), left.Lane((int)lanes.W));
        }

        var right = values[instruction.Right];
        var mask = instruction.Operation == ComputeOperation.Select ? values[instruction.Mask] : default;
        var kind = Kind(instruction.Left);

        return new ComputeVector(
            EvaluateLane(instruction.Operation, kind, left.X, right.X, mask.X),
            EvaluateLane(instruction.Operation, kind, left.Y, right.Y, mask.Y),
            EvaluateLane(instruction.Operation, kind, left.Z, right.Z, mask.Z),
            EvaluateLane(instruction.Operation, kind, left.W, right.W, mask.W));
    }

    private static uint EvaluateLane(ComputeOperation operation, ComputeValueKind kind, uint left, uint right, uint mask)
    {
        if (operation == ComputeOperation.Select)
        {
            return mask != 0 ? left : right;
        }

        if (kind == ComputeValueKind.Float32)
        {
            var a = BitConverter.UInt32BitsToSingle(left);
            var b = BitConverter.UInt32BitsToSingle(right);

            return operation switch
            {
                ComputeOperation.Add => BitConverter.SingleToUInt32Bits(a + b),
                ComputeOperation.Subtract => BitConverter.SingleToUInt32Bits(a - b),
                ComputeOperation.Multiply => BitConverter.SingleToUInt32Bits(a * b),
                ComputeOperation.LessThan => a < b ? 1u : 0u,
                ComputeOperation.Equal => a == b ? 1u : 0u,
                _ => throw new InvalidOperationException("Invalid float operation.")
            };
        }

        return operation switch
        {
            ComputeOperation.Add => unchecked(left + right),
            ComputeOperation.Subtract => unchecked(left - right),
            ComputeOperation.Multiply => unchecked(left * right),
            ComputeOperation.And => left & right,
            ComputeOperation.Or => left | right,
            ComputeOperation.Xor => left ^ right,
            ComputeOperation.LessThan => left < right ? 1u : 0u,
            ComputeOperation.Equal => left == right ? 1u : 0u,
            _ => throw new InvalidOperationException("Invalid unsigned operation.")
        };
    }

    /// <summary>Emit a bounded typed compute kernel for the existing 103-word R32_UINT adapter.</summary>
    public string EmitHlsl()
    {
        var source = new StringBuilder(
            "Buffer<uint> Input : register(t0);\n" +
            "RWBuffer<uint> Output : register(u0);\n" +
            "[numthreads(1,1,1)]\n" +
            "void main()\n{\n");

        for (var index = 0; index < _registers.Length; index++)
        {
            source.AppendLine(_registers[index] == ComputeValueKind.Float32
                ? $"    precise float4 r{index} = float4(0,0,0,0);"
                : $"    uint4 r{index} = uint4(0,0,0,0);");
        }

        for (var index = 0; index < _inputs.Length; index++)
        {
            var register = _inputs[index];
            var start = index * 4;

            var expression =
                $"uint4(Input[{start}],Input[{start + 1}]," +
                $"Input[{start + 2}],Input[{start + 3}])";

            source.AppendLine(
                $"    r{register} = {FromBits(register, expression)};");
        }

        source.AppendLine("    uint pc = 0u;");
        source.AppendLine("    bool done = false;");

        source.AppendLine(
            $"    [loop] for (uint stepIndex = 0u; " +
            $"stepIndex < {_budget}u; ++stepIndex)");

        source.AppendLine("    {");
        source.AppendLine("        uint currentPc = pc;");

        for (var index = 0; index < _blocks.Length; index++)
        {
            var block = _blocks[index];

            source.AppendLine($"        if (currentPc == {index}u)");
            source.AppendLine("        {");

            foreach (var instruction in block.Instructions)
            {
                source.AppendLine(
                    $"            r{instruction.Destination} = " +
                    $"{EmitInstruction(instruction)};");
            }

            var end = block.Terminator;

            var transfer = end.Transfer switch
            {
                ComputeTransfer.Return => "done = true;",
                ComputeTransfer.Jump => $"pc = {end.Target}u;",
                ComputeTransfer.Branch =>
                    $"pc = r{end.Condition}.x != 0u " +
                    $"? {end.Target}u : {end.Alternative}u;",
                _ => throw new InvalidOperationException(
                    "Invalid compute transfer.")
            };

            source.AppendLine($"            {transfer}");
            source.AppendLine("        }");
        }

        source.AppendLine("        if (done)");
        source.AppendLine("        {");
        source.AppendLine("            break;");
        source.AppendLine("        }");
        source.AppendLine("    }");

        // Failed execution must not publish partial results.

        source.AppendLine("    if (!done)");
        source.AppendLine("    {");

        source.AppendLine(
            $"        [unroll] for (uint i = 0u; i < {StatusWord}u; ++i)");

        source.AppendLine("        {");
        source.AppendLine("            Output[i] = 0u;");
        source.AppendLine("        }");

        source.AppendLine($"        Output[{StatusWord}] = 0u;");
        source.AppendLine("        return;");
        source.AppendLine("    }");

        // Publish successful execution results.

        for (var index = 0; index < _outputs.Length; index++)
        {
            var register = _outputs[index];

            var expression = Kind(register) == ComputeValueKind.Float32
                ? $"asuint(r{register})"
                : $"r{register}";

            source.AppendLine(
                $"    uint4 packed{index} = {expression};");

            for (var lane = 0; lane < 4; lane++)
            {
                source.AppendLine(
                    $"    Output[{index * 4 + lane}] = " +
                    $"packed{index}.{LaneNames[lane]};");
            }
        }

        source.AppendLine(
            $"    [unroll] for (uint unusedIndex = {_outputs.Length * 4}u; " +
            $"unusedIndex < {StatusWord}u; ++unusedIndex)");

        source.AppendLine("    {");
        source.AppendLine("        Output[unusedIndex] = 0u;");
        source.AppendLine("    }");

        source.AppendLine($"    Output[{StatusWord}] = 1u;");
        source.AppendLine("}");

        var result = source.ToString();

        // Temporary diagnostic for R66 validation.
        File.WriteAllText("R66-generated.hlsl", result);

        return result;
    }

    private string FromBits(int register, string expression)
    {
        return Kind(register) == ComputeValueKind.Float32 ? $"asfloat({expression})" : expression;
    }

    private string EmitInstruction(ComputeInstruction instruction)
    {
        var left = $"r{instruction.Left}";
        var right = $"r{instruction.Right}";
        var literal = instruction.Literal;

        switch (instruction.Operation)
        {
            case ComputeOperation.Constant:
                return FromBits(instruction.Destination,
                    $"uint4(0x{literal.X:X8}u,0x{literal.Y:X8}u,0x{literal.Z:X8}u,0x{literal.W:X8}u)");
            case ComputeOperation.Copy:
                return left;
            case ComputeOperation.Bitcast:
                return Kind(instruction.Destination) == ComputeValueKind.Float32 ? $"asfloat({left})" : $"asuint({left})";
            case ComputeOperation.Swizzle:
                return $"{left}.{LaneNames[(int)literal.X]}{LaneNames[(int)literal.Y]}{LaneNames[(int)literal.Z]}{LaneNames[(int)literal.W]}";
            case ComputeOperation.LessThan:
            case ComputeOperation.Equal:
                var comparison = instruction.Operation == ComputeOperation.LessThan ? "<" : "==";

                return "uint4(" + string.Join(",", LaneNames.Select(lane => $"({left}.{lane} {comparison} {right}.{lane}) ? 1u : 0u")) + ")";
            case ComputeOperation.Select:
                var type = Kind(instruction.Destination) == ComputeValueKind.Float32 ? "float4" : "uint4";
                var mask = $"r{instruction.Mask}";

                return type + "(" + string.Join(",", LaneNames.Select(lane => $"{mask}.{lane} != 0u ? {left}.{lane} : {right}.{lane}")) + ")";
        }

        var operation = instruction.Operation switch
        {
            ComputeOperation.Add => "+",
            ComputeOperation.Subtract => "-",
            ComputeOperation.Multiply => "*",
            ComputeOperation.And => "&",
            ComputeOperation.Or => "|",
            ComputeOperation.Xor => "^",
            _ => throw new InvalidOperationException("Invalid compute operation.")
        };

        return $"{left} {operation} {right}";
    }
}
