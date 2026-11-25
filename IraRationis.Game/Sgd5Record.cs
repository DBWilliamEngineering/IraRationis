// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-game/src/sgd5.rs.

using System.IO.Compression;

namespace IraRationis.Game;

public sealed record Sgd5Record(int Offset, string? Name, byte Kind, ReadOnlyMemory<byte> Packed, int UnpackedSize)
{
    public byte[]? Unpack()
    {
        if (UnpackedSize is < 0 or > (64 << 20))
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(Packed.ToArray(), false);
            using var inflater = new DeflateStream(input, CompressionMode.Decompress);
            var output = new byte[UnpackedSize];
            var count = 0;

            while (count < output.Length)
            {
                var read = inflater.Read(output.AsSpan(count));

                if (read == 0)
                {
                    return null;
                }

                count += read;
            }

            if (inflater.ReadByte() != -1)
            {
                return null;
            }

            return output;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
