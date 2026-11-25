// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-loader/src/console_insn_decode.rs.

namespace IraRationis.Loader;

public enum ConsoleInstructionKind
{
    Clwb,
    Clzero,
    ClzeroLoop,
    Rdpru,
    Monitorx,
    Mwaitx,
    Rdpid,
    Extrq,
    Insertq,
    Ud2
}

public readonly record struct ConsoleInstruction(
    ConsoleInstructionKind Kind,
    int Length,
    int Destination = 0,
    int? Source = null,
    bool RegisterField = false,
    byte FieldLength = 0,
    byte FieldIndex = 0);

public static class ConsoleInstructionDecoder
{
    public static ulong ExtractQuadword(ulong value, byte length, byte index)
    {
        var bits = GetFieldLength(length);
        var field = value >> (index & 63);

        return bits == 64 ? field : field & ((1UL << bits) - 1);
    }

    public static ulong InsertQuadword(ulong destination, ulong source, byte length, byte index)
    {
        var bits = GetFieldLength(length);
        var shift = index & 63;
        var ones = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        var mask = ones << shift;

        return (destination & ~mask) | (((source & ones) << shift) & mask);
    }

    public static ConsoleInstruction? Decode(ReadOnlySpan<byte> code)
    {
        var offset = 0;
        var operandSize = false;
        var hasF3 = false;
        var repeat = 0;
        var rex = 0;

        while (offset < code.Length)
        {
            var prefix = code[offset];

            if (prefix is >= 0x40 and <= 0x4F)
            {
                rex = prefix;
                offset++;

                continue;
            }

            var isPrefix = true;

            switch (prefix)
            {
                case 0x66:
                    operandSize = true;

                    break;

                case 0xF2:
                    repeat = 0xF2;

                    break;

                case 0xF3:
                    hasF3 = true;
                    repeat = 0xF3;

                    break;

                case 0x2E:
                case 0x3E:
                case 0x26:
                case 0x36:
                case 0x64:
                case 0x65:
                case 0x67:
                    break;

                default:
                    isPrefix = false;

                    break;
            }

            if (!isPrefix)
            {
                break;
            }

            rex = 0;
            offset++;
        }

        if (code.Length - offset < 2 || code[offset] != 0x0F)
        {
            return null;
        }

        var opcode = code[offset + 1];

        if (opcode == 0x0B)
        {
            return new ConsoleInstruction(ConsoleInstructionKind.Ud2, offset + 2);
        }

        if (code.Length - offset < 3)
        {
            return null;
        }

        var modRm = code[offset + 2];
        var mode = modRm >> 6;
        var register = (modRm >> 3) & 7;
        var operand = modRm & 7;

        if (opcode == 0x01)
        {
            return DecodeSystemInstruction(code, offset, modRm);
        }

        if (opcode is 0x78 or 0x79 && mode == 3 && (repeat == 0xF2 || (repeat == 0 && operandSize)))
        {
            return DecodeSse4A(code, offset, opcode, repeat == 0xF2, register, operand, rex);
        }

        if (opcode == 0xAE && operandSize && register == 6 && mode != 3)
        {
            var length = GetModRmLength(code[(offset + 2)..]);

            if (length.HasValue)
            {
                return new ConsoleInstruction(ConsoleInstructionKind.Clwb, offset + 2 + length.Value);
            }

            return null;
        }

        if (opcode == 0xC7 && hasF3 && register == 7 && mode == 3)
        {
            var destination = operand | ((rex & 1) << 3);

            return new ConsoleInstruction(ConsoleInstructionKind.Rdpid, offset + 3, destination);
        }

        return null;
    }

    private static ConsoleInstruction? DecodeSystemInstruction(ReadOnlySpan<byte> code, int offset, byte opcode)
    {
        switch (opcode)
        {
            case 0xFC:
                if (code.Length - offset >= 12 && !code[..offset].Contains((byte)0x67)
                                               && code[offset + 3] == 0x48 && code[offset + 4] == 0x83
                                               && code[offset + 5] == 0xC0 && code[offset + 6] == 0x40
                                               && code[offset + 7] is 0x48 or 0x4C && code[offset + 8] == 0x39
                                               && (code[offset + 9] & 0xC7) == 0xC0 && code[offset + 10] == 0x72
                                               && code[offset + 11] == 0xF4)
                {
                    var endRegister = ((code[offset + 9] >> 3) & 7) | (code[offset + 7] == 0x4C ? 8 : 0);

                    return new ConsoleInstruction(ConsoleInstructionKind.ClzeroLoop, offset + 12, endRegister);
                }

                return new ConsoleInstruction(ConsoleInstructionKind.Clzero, offset + 3);

            case 0xFD:
                return new ConsoleInstruction(ConsoleInstructionKind.Rdpru, offset + 3);

            case 0xFA:
                return new ConsoleInstruction(ConsoleInstructionKind.Monitorx, offset + 3);

            case 0xFB:
                return new ConsoleInstruction(ConsoleInstructionKind.Mwaitx, offset + 3);

            default:
                return null;
        }
    }

    private static ConsoleInstruction? DecodeSse4A(
        ReadOnlySpan<byte> code,
        int offset,
        byte opcode,
        bool insert,
        int register,
        int operand,
        int rex)
    {
        var extendedRegister = register | ((rex & 4) << 1);
        var extendedOperand = operand | ((rex & 1) << 3);
        var immediate = opcode == 0x78;

        if (immediate && code.Length - offset < 5)
        {
            return null;
        }

        var length = offset + (immediate ? 5 : 3);
        var fieldLength = immediate ? code[offset + 3] : (byte)0;
        var fieldIndex = immediate ? code[offset + 4] : (byte)0;

        if (insert)
        {
            return new ConsoleInstruction(ConsoleInstructionKind.Insertq, length, extendedRegister,
                extendedOperand, !immediate, fieldLength, fieldIndex);
        }

        if (immediate)
        {
            if (register != 0)
            {
                return null;
            }

            return new ConsoleInstruction(ConsoleInstructionKind.Extrq, length, extendedOperand,
                null, false, fieldLength, fieldIndex);
        }

        return new ConsoleInstruction(ConsoleInstructionKind.Extrq, length, extendedRegister, extendedOperand);
    }

    private static int? GetModRmLength(ReadOnlySpan<byte> code)
    {
        if (code.IsEmpty)
        {
            return null;
        }

        var mode = code[0] >> 6;
        var operand = code[0] & 7;

        if (mode == 3)
        {
            return 1;
        }

        var length = 1;
        var baseRegister = operand;

        if (operand == 4)
        {
            if (code.Length < 2)
            {
                return null;
            }

            baseRegister = code[1] & 7;
            length++;
        }

        length += mode switch
        {
            0 when baseRegister == 5 => 4,
            0 => 0,
            1 => 1,
            _ => 4
        };

        return code.Length >= length ? length : null;
    }

    private static int GetFieldLength(byte length)
    {
        var bits = length & 63;

        return bits == 0 ? 64 : bits;
    }
}
