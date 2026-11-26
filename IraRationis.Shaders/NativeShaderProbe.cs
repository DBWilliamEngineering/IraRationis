// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;

namespace IraRationis.Shaders;

public static class NativeShaderProbe
{
    public static void Verify()
    {
        uint[] words = [0xbe800385, 0x80018100, 0xb002fffe, 0x8003ff01, 0x12345678, 0xbf810000];

        var code = new byte[words.Length * 4];

        for (var i = 0; i < words.Length; i++)
        {
            Write32(code, i * 4, words[i]);
        }

        var decoded = Rdna2ScalarCompiler.Decode(code);

        if (decoded.Count != 5 || decoded[0].Left != new ScalarOperand(false, 5) ||
            decoded[1].Operation != ScalarOperation.Add || decoded[1].Left != new ScalarOperand(true, 0) ||
            decoded[1].Right != new ScalarOperand(false, 1) || decoded[2].Left.Value != 0xfffffffe ||
            decoded[3].Right.Value != 0x12345678 || decoded[4].Offset != 20)
        {
            throw new InvalidOperationException("RDNA2 scalar decoding mismatch.");
        }

        var hlsl = Rdna2ScalarCompiler.LowerDiagnostic(code);

        if (!hlsl.Contains("s[0] = 0x00000005u;", StringComparison.Ordinal) ||
            !hlsl.Contains("r < a", StringComparison.Ordinal)
            || !hlsl.Contains("Output[102]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("RDNA2 scalar HLSL lowering mismatch.");
        }

        var fixture = MakeFixture(code);
        var native = NativeProgramReader.ReadPart(fixture);
        var threads = native.Count == 1 ? native[0].Threads : null;

        if (native.Count != 1 || native[0].Stage != NativeShaderStage.Compute || native[0].Wave64 ||
            threads is not { Length: 3 } || threads[0] != 8 || threads[1] != 4 || threads[2] != 1 ||
            !native[0].Code.AsSpan().SequenceEqual(code))
        {
            throw new InvalidOperationException("XDAT/XBCH/FSSM extraction mismatch.");
        }

        var bad = new byte[4];

        Write32(bad, 0, 0xbf820000); // branch, deliberately not implemented

        try
        {
            _ = Rdna2ScalarCompiler.Decode(bad);

            throw new InvalidOperationException("Unsupported branch accepted.");
        }
        catch (NotSupportedException)
        {
            // Explicit rejection is part of the supported compiler contract.
            //? Log or Write
        }

        try
        {
            _ = Rdna2ScalarCompiler.Decode(code.AsSpan(0, 16));

            throw new InvalidOperationException("Truncated literal accepted.");
        }
        catch (InvalidDataException)
        {
            //? Log or Write
        }

        try
        {
            _ = NativeProgramReader.ReadPart(fixture.AsSpan(0, fixture.Length - 1));

            throw new InvalidOperationException("Truncated XBCH accepted.");
        }
        catch (InvalidDataException)
        {
            //? Log or Write
        }
    }

    private static byte[] MakeFixture(byte[] code)
    {
        const int record = 0x68;
        const int codeAt = record + 0x54 + 0x34;

        var payload = new byte[codeAt + code.Length];

        Write64(payload, 0, 6);

        foreach (var offset in new[] { 8, 16, 24, 32, 40, 48, 0x48, 0x60 })
        {
            Write64(payload, offset, 0xDEADBEEFDEADBEEF);
        }

        Write64(payload, 0x60, record);
        Write64(payload, record, codeAt);
        Write64(payload, record + 8, (ulong)code.Length);
        Write32(payload, record + 0x14, 32);
        Write32(payload, record + 0x50, 16);
        Write32(payload, record + 0x54 + 0x28, 8);
        Write32(payload, record + 0x54 + 0x2c, 4);
        Write32(payload, record + 0x54 + 0x30, 1);

        code.CopyTo(payload, codeAt);

        var raw = new byte[16 + payload.Length];

        "FSSM"u8.CopyTo(raw);

        Write32(raw, 4, 0x101);
        Write64(raw, 8, (ulong)payload.Length);

        payload.CopyTo(raw, 16);

        using var stream = new MemoryStream();

        using (var zip = new ZLibStream(stream, CompressionLevel.Fastest, true))
        {
            zip.Write(raw);
        }

        var compressed = stream.ToArray();
        var part = new byte[48 + 24 + compressed.Length];

        "XDAT"u8.CopyTo(part.AsSpan(24));

        Write32(part, 28, 1);
        Write32(part, 32, 0x24);
        Write32(part, 36, 0x0d);
        Write32(part, 40, 48);
        Write32(part, 44, (uint)(24 + compressed.Length));

        "XBCH"u8.CopyTo(part.AsSpan(48));

        Write32(part, 52, 3);
        Write64(part, 56, (ulong)compressed.Length);
        Write64(part, 64, (ulong)raw.Length);

        compressed.CopyTo(part, 72);

        return part;
    }

    private static void Write32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void Write64(byte[] data, int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), value);
    }
}
