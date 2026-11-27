// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics.Shaders.Models;

namespace IraRationis.Graphics;

internal static class TextureStripDepthPolicy
{
    internal static bool TryNormalize(GraphicsPipelineMetadata metadata, out GraphicsPipelineMetadata normalized)
    {
        normalized = metadata;

        var depth = metadata.State.Where(static record => record.Kind == 21).ToArray();
        var format = metadata.State.Where(static record => record.Kind == 16).ToArray();

        if (depth.Length != 1 || format.Length != 1 || depth[0].Bytes.Length != 64 || format[0].Bytes.Length != 8 ||
            ReadWord(depth[0].Bytes, 0) != 21 || ReadWord(format[0].Bytes, 0) != 16 ||
            ReadWord(depth[0].Bytes, 4) != 0 || ReadWord(depth[0].Bytes, 8) != 0 ||
            ReadWord(depth[0].Bytes, 16) != 0 || ReadWord(depth[0].Bytes, 56) != 0 ||
            ReadWord(format[0].Bytes, 4) is not (0 or 20))
        {
            return false;
        }

        if (ReadWord(format[0].Bytes, 4) == 0)
        {
            return true;
        }

        // Do not mutate the guest PSO, or its shared-cache identity. The host
        // has no DSV bound for this fully depth/stencil-inactive shader.
        var hostFormat = (byte[])format[0].Bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hostFormat.AsSpan(4), 0);
        var records = metadata.State.Select(record => record.Kind == 16
            ? new PipelineStateRecord(16, hostFormat) : record).ToList();
        normalized = metadata with { State = records };

        return true;
    }

    private static uint ReadWord(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    }
}
