// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace IraRationis.Core;

public static class BinaryData
{
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, long offset, long size)
    {
        if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
        {
            throw new InvalidDataException("Binary range outside file.");
        }

        return data.Slice((int)offset, (int)size);
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> data, long offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(Slice(data, offset, 2));
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> data, long offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
    }

    public static ulong ReadUInt64(ReadOnlySpan<byte> data, long offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(Slice(data, offset, 8));
    }

    public static string ReadNullTerminatedString(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);

        return end < 0 
            ? throw new InvalidDataException("Unterminated string.") 
            : Encoding.UTF8.GetString(data[..end]);
    }
}
