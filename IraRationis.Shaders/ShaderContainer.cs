// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-shader/src/dxbc.rs and dxil.rs, Zettrox/ztrx.

using System.Text;
using IraRationis.Core;

namespace IraRationis.Shaders;

public sealed record ShaderContainer(byte[] Digest, ushort Major, ushort Minor, IReadOnlyList<ShaderPart> Parts)
{
    public ShaderPart? Part(string fourCC)
    {
        return Parts.FirstOrDefault(part => part.FourCC == fourCC);
    }

    public static ShaderContainer Parse(ReadOnlyMemory<byte> memory)
    {
        var data = memory.Span;

        if (data.Length < 32 || !data[..4].SequenceEqual("DXBC"u8))
        {
            throw new InvalidDataException("Not DXBC.");
        }

        var total = BinaryData.ReadUInt32(data, 24);

        if (total < 32 || total > data.Length)
        {
            throw new InvalidDataException("Invalid DXBC size.");
        }

        memory = memory[..(int)total];
        data = memory.Span;

        var count = BinaryData.ReadUInt32(data, 28);

        if (count > 256 || 32L + 4L * count > total)
        {
            throw new InvalidDataException("Invalid DXBC part table.");
        }

        var parts = new List<ShaderPart>((int)count);

        for (var i = 0; i < count; i++)
        {
            var offset = BinaryData.ReadUInt32(data, 32 + 4L * i);

            if (offset < 32L + 4L * count)
            {
                throw new InvalidDataException("DXBC part overlaps its header or offset table.");
            }

            var tag = Encoding.Latin1.GetString(BinaryData.Slice(data, offset, 4));
            var size = BinaryData.ReadUInt32(data, offset + 4L);

            BinaryData.Slice(data, offset + 8L, size);

            var payload = memory.Slice((int)(offset + 8L), (int)size);

            parts.Add(new ShaderPart(tag, offset, payload));
        }

        return new ShaderContainer(
            data.Slice(4, 16).ToArray(),
            BinaryData.ReadUInt16(data, 20),
            BinaryData.ReadUInt16(data, 22),
            parts.AsReadOnly());
    }
}
