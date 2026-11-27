// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using IraRationis.Shaders.Scalar.Models;

namespace IraRationis.Shaders.Scalar;

/// <summary>
///     Bounded, straight-line scalar SSA graph. Each output retains its own value
///     identity, including SCC. No control flow, vector or wave semantics are implied.
/// </summary>
public sealed class ScalarValueGraph
{
    private readonly Dictionary<ScalarValueNode, int> _interned = new();
    private readonly bool[] _live;
    private readonly List<ScalarValueNode> _nodes = new();
    private readonly int[] _outputs = new int[ScalarReferenceExecutor.StateWords];

    private ScalarValueGraph(IReadOnlyList<ScalarInstruction> instructions)
    {
        for (var index = 0; index < _outputs.Length; index++)
        {
            _outputs[index] = Intern(new ScalarValueNode(ScalarValueKind.Input, -1, -1, (uint)index));
        }

        foreach (var instruction in instructions)
        {
            if (instruction.Operation == ScalarOperation.End)
            {
                break;
            }

            if (instruction.Operation == ScalarOperation.Nop)
            {
                continue;
            }

            var left = Operand(instruction.Left);

            if (instruction.Operation == ScalarOperation.Move)
            {
                _outputs[instruction.Destination] = left;

                continue;
            }

            var right = Operand(instruction.Right);
            var kind = instruction.Operation switch
            {
                ScalarOperation.Add => ScalarValueKind.Add,
                ScalarOperation.Subtract => ScalarValueKind.Subtract,
                ScalarOperation.And => ScalarValueKind.And,
                ScalarOperation.Or => ScalarValueKind.Or,
                ScalarOperation.Xor => ScalarValueKind.Xor,
                _ => throw new InvalidDataException("Unsupported scalar graph operation.")
            };
            var result = Binary(kind, left, right);
            var condition = kind switch
            {
                ScalarValueKind.Add => Binary(ScalarValueKind.Less, result, left),
                ScalarValueKind.Subtract => Binary(ScalarValueKind.Less, left, right),
                _ => Binary(ScalarValueKind.NotEqual, result, Constant(0))
            };

            _outputs[instruction.Destination] = result;
            _outputs[ScalarReferenceExecutor.RegisterCount] = condition;
        }

        _live = new bool[_nodes.Count];

        foreach (var output in _outputs)
        {
            _live[output] = true;
        }

        // Every input to a node predates that node. Reverse propagation therefore
        // marks the entire dependency closure without recursion or graph mutation.
        for (var index = _nodes.Count - 1; index >= 0; index--)
        {
            if (!_live[index])
            {
                continue;
            }

            var node = _nodes[index];

            if (node.Left >= 0)
            {
                _live[node.Left] = true;
                _live[node.Right] = true;
            }
        }
    }

    public int ValueCount
    {
        get
        {
            return _nodes.Count;
        }
    }

    public int LiveValueCount
    {
        get
        {
            return _live.Count(static live => live);
        }
    }

    public static ScalarValueGraph Build(ReadOnlySpan<byte> code)
    {
        if (code.Length > ScalarReferenceExecutor.MaximumCodeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        return new ScalarValueGraph(Rdna2ScalarCompiler.Decode(code));
    }

    public uint[] Evaluate(ReadOnlySpan<uint> initial)
    {
        if (initial.Length != _outputs.Length || initial[ScalarReferenceExecutor.RegisterCount] > 1)
        {
            throw new ArgumentException("Invalid scalar graph input.", nameof(initial));
        }

        var values = new uint[_nodes.Count];

        for (var index = 0; index < _nodes.Count; index++)
        {
            if (!_live[index])
            {
                continue;
            }

            var node = _nodes[index];

            values[index] = node.Operation switch
            {
                ScalarValueKind.Input => initial[(int)node.Value],
                ScalarValueKind.Constant => node.Value,
                _ => Calculate(node.Operation, values[node.Left], values[node.Right])
            };
        }

        var result = new uint[_outputs.Length];

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = values[_outputs[index]];
        }

        return result;
    }

    /// <summary>Original typed-buffer compute contract; not a replacement for arbitrary guest shaders.</summary>
    public string EmitTypedCompute()
    {
        var source = new StringBuilder("Buffer<uint> Input : register(t0);\n" +
                                       "RWBuffer<uint> Output : register(u0);\n[numthreads(1,1,1)]\nvoid main()\n{\n");

        for (var index = 0; index < _nodes.Count; index++)
        {
            if (!_live[index])
            {
                continue;
            }

            var node = _nodes[index];
            var expression = node.Operation switch
            {
                ScalarValueKind.Input => $"Input[{node.Value}]",
                ScalarValueKind.Constant => $"0x{node.Value:X8}u",
                ScalarValueKind.Less => $"(v{node.Left} < v{node.Right}) ? 1u : 0u",
                ScalarValueKind.NotEqual => $"(v{node.Left} != v{node.Right}) ? 1u : 0u",
                _ => $"v{node.Left} {Operator(node.Operation)} v{node.Right}"
            };

            source.AppendLine($"    uint v{index} = {expression};");
        }

        for (var index = 0; index < _outputs.Length; index++)
        {
            source.AppendLine($"    Output[{index}] = v{_outputs[index]};");
        }

        source.AppendLine("}");

        return source.ToString();
    }

    private int Operand(ScalarOperand operand)
    {
        return operand.IsRegister ? _outputs[(int)operand.Value] : Constant(operand.Value);
    }

    private int Constant(uint value)
    {
        return Intern(new ScalarValueNode(ScalarValueKind.Constant, -1, -1, value));
    }

    private int Binary(ScalarValueKind kind, int left, int right)
    {
        var leftNode = _nodes[left];
        var rightNode = _nodes[right];

        if (leftNode.Operation == ScalarValueKind.Constant && rightNode.Operation == ScalarValueKind.Constant)
        {
            return Constant(Calculate(kind, leftNode.Value, rightNode.Value));
        }

        if (kind is ScalarValueKind.Add or ScalarValueKind.And or ScalarValueKind.Or or ScalarValueKind.Xor
                or ScalarValueKind.NotEqual && left > right)
        {
            (left, right) = (right, left);
        }

        return Intern(new ScalarValueNode(kind, left, right, 0));
    }

    private int Intern(ScalarValueNode node)
    {
        if (_interned.TryGetValue(node, out var index))
        {
            return index;
        }

        index = _nodes.Count;

        _nodes.Add(node);
        _interned.Add(node, index);

        return index;
    }

    private static uint Calculate(ScalarValueKind kind, uint left, uint right)
    {
        return kind switch
        {
            ScalarValueKind.Add => unchecked(left + right),
            ScalarValueKind.Subtract => unchecked(left - right),
            ScalarValueKind.And => left & right,
            ScalarValueKind.Or => left | right,
            ScalarValueKind.Xor => left ^ right,
            ScalarValueKind.Less => left < right ? 1U : 0U,
            ScalarValueKind.NotEqual => left != right ? 1U : 0U,
            _ => throw new InvalidOperationException("Invalid scalar value operation.")
        };
    }

    private static string Operator(ScalarValueKind kind)
    {
        return kind switch
        {
            ScalarValueKind.Add => "+",
            ScalarValueKind.Subtract => "-",
            ScalarValueKind.And => "&",
            ScalarValueKind.Or => "|",
            ScalarValueKind.Xor => "^",
            _ => throw new InvalidOperationException("Invalid scalar binary operator.")
        };
    }
}
