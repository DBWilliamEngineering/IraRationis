// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace IraRationis.Shaders;

/// <summary>
///     Bounded, straight-line scalar SSA graph. Each output retains its own value
///     identity, including SCC. No control flow, vector or wave semantics are implied.
/// </summary>
public sealed class ScalarValueGraph
{
    private readonly List<Node> _nodes = new();
    private readonly Dictionary<Node, int> _interned = new();
    private readonly int[] _outputs = new int[ScalarReferenceExecutor.StateWords];
    private readonly bool[] _live;

    private ScalarValueGraph(IReadOnlyList<ScalarInstruction> instructions)
    {
        for (var index = 0; index < _outputs.Length; index++)
        {
            _outputs[index] = Intern(new Node(Kind.Input, -1, -1, (uint)index));
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
                ScalarOperation.Add => Kind.Add,
                ScalarOperation.Subtract => Kind.Subtract,
                ScalarOperation.And => Kind.And,
                ScalarOperation.Or => Kind.Or,
                ScalarOperation.Xor => Kind.Xor,
                _ => throw new InvalidDataException("Unsupported scalar graph operation.")
            };
            var result = Binary(kind, left, right);
            var condition = kind switch
            {
                Kind.Add => Binary(Kind.Less, result, left),
                Kind.Subtract => Binary(Kind.Less, left, right),
                _ => Binary(Kind.NotEqual, result, Constant(0))
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
                Kind.Input => initial[(int)node.Value],
                Kind.Constant => node.Value,
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
                Kind.Input => $"Input[{node.Value}]",
                Kind.Constant => $"0x{node.Value:X8}u",
                Kind.Less => $"(v{node.Left} < v{node.Right}) ? 1u : 0u",
                Kind.NotEqual => $"(v{node.Left} != v{node.Right}) ? 1u : 0u",
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
        return Intern(new Node(Kind.Constant, -1, -1, value));
    }

    private int Binary(Kind kind, int left, int right)
    {
        var a = _nodes[left];
        var b = _nodes[right];

        if (a.Operation == Kind.Constant && b.Operation == Kind.Constant)
        {
            return Constant(Calculate(kind, a.Value, b.Value));
        }

        if ((kind is Kind.Add or Kind.And or Kind.Or or Kind.Xor or Kind.NotEqual) && left > right)
        {
            (left, right) = (right, left);
        }

        return Intern(new Node(kind, left, right, 0));
    }

    private int Intern(Node node)
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

    private static uint Calculate(Kind kind, uint left, uint right)
    {
        return kind switch
        {
            Kind.Add => unchecked(left + right),
            Kind.Subtract => unchecked(left - right),
            Kind.And => left & right,
            Kind.Or => left | right,
            Kind.Xor => left ^ right,
            Kind.Less => left < right ? 1U : 0U,
            Kind.NotEqual => left != right ? 1U : 0U,
            _ => throw new InvalidOperationException("Invalid scalar value operation.")
        };
    }

    private static string Operator(Kind kind)
    {
        return kind switch
        {
            Kind.Add => "+",
            Kind.Subtract => "-",
            Kind.And => "&",
            Kind.Or => "|",
            Kind.Xor => "^",
            _ => throw new InvalidOperationException("Invalid scalar binary operator.")
        };
    }

    private enum Kind
    {
        Input,
        Constant,
        Add,
        Subtract,
        And,
        Or,
        Xor,
        Less,
        NotEqual
    }

    private readonly record struct Node(Kind Operation, int Left, int Right, uint Value);
}
