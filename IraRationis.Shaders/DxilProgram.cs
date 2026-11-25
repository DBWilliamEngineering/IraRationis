// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-shader/src/dxbc.rs and dxil.rs, Zettrox/ztrx.

using IraRationis.Core;

namespace IraRationis.Shaders;

public sealed record DxilProgram(
    ShaderKind Kind,
    byte Major,
    byte Minor,
    uint DxilVersion,
    ReadOnlyMemory<byte> Bitcode,
    ReadOnlyMemory<byte> Prefix,
    ReadOnlyMemory<byte> Trailer)
{
    private static ReadOnlySpan<byte> LlvmBitcodeMagic
    {
        get
        {
            return [0x42, 0x43, 0xC0, 0xDE];
        }
    }

    public bool IsStandardLayout
    {
        get
        {
            return Prefix.IsEmpty && Trailer.IsEmpty;
        }
    }

    public bool HasXdat
    {
        get
        {
            return Prefix.Span.StartsWith("XDAT"u8);
        }
    }

    public bool HasXbop
    {
        get
        {
            return Trailer.Span.IndexOf("xbop"u8) >= 0;
        }
    }

    public static DxilProgram Parse(ReadOnlyMemory<byte> memory)
    {
        var data = memory.Span;
        var version = BinaryData.ReadUInt32(data, 0);
        var size = 4L * BinaryData.ReadUInt32(data, 4);

        if (size < 24 || size > data.Length)
        {
            throw new InvalidDataException("Invalid DXIL program size.");
        }

        memory = memory[..(int)size];
        data = memory.Span;

        if (!data.Slice(8, 4).SequenceEqual("DXIL"u8))
        {
            throw new InvalidDataException("Missing DXIL magic.");
        }

        var start = 8L + BinaryData.ReadUInt32(data, 16);
        var length = BinaryData.ReadUInt32(data, 20);

        if (start < 24 || !BinaryData.Slice(data, start, length).StartsWith(LlvmBitcodeMagic))
        {
            throw new InvalidDataException("Invalid LLVM bitcode.");
        }

        var end = (int)(start + length);

        return new DxilProgram(
            (ShaderKind)(version >> 16),
            (byte)((version >> 4) & 15),
            (byte)(version & 15),
            BinaryData.ReadUInt32(data, 12),
            memory.Slice((int)start, (int)length),
            memory.Slice(24, (int)start - 24),
            memory[end..]);
    }
}
