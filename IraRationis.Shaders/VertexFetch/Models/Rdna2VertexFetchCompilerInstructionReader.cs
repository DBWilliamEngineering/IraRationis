// SPDX-License-Identifier: GPL-3.0-or-later
// GFX10 instruction encodings and P0 interpolation semantics cross-checked
// against LLVM AMDGPU and VIctory/victory-fork gfx10 and emit/lower.rs.


namespace IraRationis.Shaders.VertexFetch.Models;

internal ref struct Rdna2VertexFetchCompilerInstructionReader(ReadOnlySpan<byte> code)
{
    private readonly ReadOnlySpan<byte> _code = code;
    private int _offset;

    public uint Read()
    {
        var result = Rdna2VertexFetchCompiler.Word(_code, _offset);

        _offset += 4;

        return result;
    }

    public void Expect(string operation, scoped ReadOnlySpan<uint> words)
    {
        foreach (var expected in words)
        {
            var offset = _offset;
            var actual = Read();

            if (actual != expected)
            {
                throw new NotSupportedException($"{operation}: unexpected word 0x{actual:X8} at 0x{offset:X}.");
            }
        }
    }

    public void SkipPrologue()
    {
        // VOP3 opcode 0x180 is VOP1 v_nop. The encoded operands can
        // include one literal DWORD, even though v_nop does not use it.
        while (_offset <= _code.Length - 8
               && (Rdna2VertexFetchCompiler.Word(_code, _offset) & 0xffff0000U) == 0xd5800000U)
        {
            _ = Read();

            var operands = Read();

            if ((operands & 511U) == 255 || ((operands >> 9) & 511U) == 255 ||
                ((operands >> 18) & 511U) == 255)
            {
                _ = Read();
            }
        }

        if (_offset <= _code.Length - 4 && (Rdna2VertexFetchCompiler.Word(_code, _offset) & 0xffff0000U) == 0xbfa00000U)
        {
            _ = Read(); // s_inst_prefetch: no architectural shader result.
        }
    }

    public void End()
    {
        Expect("endpgm", [0xbf810000]);

        while (_offset < _code.Length)
        {
            Expect("post-termination padding", [0xbf9f0000]);
        }
    }
}
