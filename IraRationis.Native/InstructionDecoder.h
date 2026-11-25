// SPDX-License-Identifier: GPL-3.0-or-later
// Based on Zettrox/ztrx console_insn_decode.rs, originally XCL (GPL-2.0-only).
#pragma once
#include <cstddef>
#include <cstdint>

namespace irx
{
    enum class Kind { Unknown, Clwb, Clzero, ClzeroLoop, Rdpru, Monitorx, Mwaitx, Rdpid, Extrq, Insertq, Ud2 };

    struct Instruction
    {
        Kind kind = Kind::Unknown;
        unsigned length = 0;
        unsigned destination = 0;
        int source = -1;
        bool registerField = false;
        unsigned fieldLength = 0;
        unsigned fieldIndex = 0;
        bool address32 = false;
    };

    inline std::uint64_t Mask(unsigned length) noexcept
    {
        length &= 63;
        return length == 0 ? UINT64_MAX : (UINT64_C(1) << length) - 1;
    }

    inline std::uint64_t Extract(std::uint64_t value, unsigned length, unsigned index) noexcept
    {
        return (value >> (index & 63)) & Mask(length);
    }

    inline std::uint64_t Insert(std::uint64_t destination, std::uint64_t source, unsigned length,
                                unsigned index) noexcept
    {
        const auto mask = Mask(length) << (index & 63);
        return (destination & ~mask) | ((source << (index & 63)) & mask);
    }

    inline Instruction Decode(const std::uint8_t* code, std::size_t size) noexcept
    {
        unsigned i = 0, rex = 0, repeat = 0;
        bool operandSize = false, address32 = false;
        while (i < size)
        {
            const auto prefix = code[i];
            if (prefix >= 0x40 && prefix <= 0x4f)
            {
                rex = prefix;
                ++i;
                continue;
            }
            if (prefix == 0x66)
            {
                operandSize = true;
            }
            else if (prefix == 0xf2 || prefix == 0xf3)
            {
                repeat = prefix;
            }
            else if (prefix == 0x67)
            {
                address32 = true;
            }
            else if (prefix != 0x2e && prefix != 0x3e && prefix != 0x26 && prefix != 0x36 && prefix != 0x64 && prefix !=
                0x65)
            {
                break;
            }
            rex = 0;
            ++i;
        }
        if (size - i < 2 || code[i] != 0x0f)
        {
            return {};
        }
        const auto op = code[i + 1];
        if (op == 0x0b)
        {
            return {Kind::Ud2, i + 2};
        }
        if (size - i < 3)
        {
            return {};
        }
        const unsigned m = code[i + 2], mode = m >> 6, reg = (m >> 3) & 7, rm = m & 7;
        if (op == 1)
        {
            switch (m)
            {
            case 0xfc:
                if (!address32 && size - i >= 12 && code[i + 3] == 0x48 && code[i + 4] == 0x83 && code[i + 5] == 0xc0 &&
                    code[i + 6] == 0x40 && (code[i + 7] == 0x48 || code[i + 7] == 0x4c) && code[i + 8] == 0x39 && (code[
                        i + 9] & 0xc7) == 0xc0 && code[i + 10] == 0x72 && code[i + 11] == 0xf4)
                {
                    return {Kind::ClzeroLoop, i + 12, ((code[i + 9] >> 3) & 7U) | (code[i + 7] == 0x4c ? 8U : 0U)};
                }
                return {Kind::Clzero, i + 3, 0, -1, false, 0, 0, address32};
            case 0xfd: return {Kind::Rdpru, i + 3};
            case 0xfa: return {Kind::Monitorx, i + 3};
            case 0xfb: return {Kind::Mwaitx, i + 3};
            default: return {};
            }
        }
        if ((op == 0x78 || op == 0x79) && mode == 3 && (repeat == 0xf2 || (repeat == 0 && operandSize)))
        {
            const bool immediate = op == 0x78;
            if (immediate && size - i < 5)
            {
                return {};
            }
            const auto dst = reg | ((rex & 4) << 1);
            const auto src = rm | ((rex & 1) << 3);
            const auto length = i + (immediate ? 5U : 3U);
            const unsigned fieldLength = immediate ? code[i + 3] : 0;
            const unsigned fieldIndex = immediate ? code[i + 4] : 0;
            if (repeat == 0xf2)
            {
                return {Kind::Insertq, length, dst, static_cast<int>(src), !immediate, fieldLength, fieldIndex};
            }
            if (immediate)
            {
                if (reg != 0)
                {
                    return {};
                }
                return {Kind::Extrq, length, src, -1, false, fieldLength, fieldIndex};
            }
            return {Kind::Extrq, length, dst, static_cast<int>(src)};
        }
        if (op == 0xae && operandSize && reg == 6 && mode != 3)
        {
            unsigned length = 1, base = rm;
            if (rm == 4)
            {
                if (size - i < 4)
                {
                    return {};
                }
                base = code[i + 3] & 7;
                ++length;
            }
            length += mode == 0 ? (base == 5 ? 4 : 0) : (mode == 1 ? 1 : 4);
            if (size - i >= 2 + length)
            {
                return {Kind::Clwb, i + 2 + length};
            }
        }
        if (op == 0xc7 && repeat == 0xf3 && reg == 7 && mode == 3)
        {
            return {Kind::Rdpid, i + 3, rm | ((rex & 1) << 3)};
        }
        return {};
    }
}
