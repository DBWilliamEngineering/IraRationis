// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;

namespace IraRationis.HLE;

internal static class DirectStoragePayload
{
    internal static bool IsRawLengthValid(int source, int destination, bool strict)
    {
        return source >= 0 && destination >= source && (!strict || source == destination);
    }

    // Length validation is distinct from proving a complete compressed stream.
    // ZLibStream owns the decompressor and performs its normal format checks.
    internal static bool TryInflate(byte[] source, Span<byte> destination, bool strict, out int written)
    {
        written = 0;

        try
        {
            using var input = new MemoryStream(source, false);
            using var inflater = new ZLibStream(input, CompressionMode.Decompress);

            while (written < destination.Length)
            {
                var read = inflater.Read(destination[written..]);

                if (read == 0)
                {
                    break;
                }

                written += read;
            }

            if (inflater.ReadByte() != -1)
            {
                return false;
            }

            return !strict || written == destination.Length;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}
