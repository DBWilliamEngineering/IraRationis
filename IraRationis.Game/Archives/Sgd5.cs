// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-game/src/sgd5.rs.

using System.Buffers.Binary;
using System.Text;

namespace IraRationis.Game.Archives;

public static class Sgd5
{
    private const int MaxUnpackedSize = 64 << 20;
    private static readonly byte[] Marker = [0xE7, 0xDC, 0x0D, 0];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsSgd5(ReadOnlySpan<byte> file)
    {
        return file.IndexOf("SGD5"u8) >= 0;
    }

    public static IEnumerable<Sgd5Record> Records(ReadOnlyMemory<byte> file)
    {
        var position = 0;

        while (position < file.Length)
        {
            var relative = FindMarker(file[position..]);

            if (relative < 0)
            {
                yield break;
            }

            var offset = position + relative;
            var record = ReadRecord(file, offset);

            if (record is not null)
            {
                yield return record;
            }

            position = offset + 4;
        }
    }

    private static int FindMarker(ReadOnlyMemory<byte> file)
    {
        return file.Span.IndexOf(Marker);
    }

    private static Sgd5Record? ReadRecord(ReadOnlyMemory<byte> file, int offset)
    {
        if (offset < 6 || offset > file.Length - 12)
        {
            return null;
        }

        var bytes = file.Span;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset - 4)..]);
        var repeatedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
        var unpacked = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 8)..]);

        if (size != repeatedSize || size < 12 || size > file.Length - offset || unpacked > MaxUnpackedSize)
        {
            return null;
        }

        var terminator = offset - 6;
        var name = bytes[terminator] == 0 ? ReadName(bytes, terminator) : null;
        var kind = bytes[offset - 5];
        var packed = file.Slice(offset + 12, (int)size - 12);

        return new Sgd5Record(offset, name, kind, packed, (int)unpacked);
    }

    private static string? ReadName(ReadOnlySpan<byte> file, int terminator)
    {
        var start = terminator;

        while (start > 0 && file[start - 1] is >= 0x21 and <= 0x7E)
        {
            start--;
        }

        for (var index = Math.Max(0, start - 1); index < terminator; index++)
        {
            if (file[index] != terminator - index)
            {
                continue;
            }

            try
            {
                return StrictUtf8.GetString(file[(index + 1)..terminator]);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        return null;
    }
}
