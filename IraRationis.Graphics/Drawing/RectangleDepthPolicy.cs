// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics.Shaders.Models;

namespace IraRationis.Graphics.Drawing;

internal static class RectangleDepthPolicy
{
    internal static bool TryGetHostState(GraphicsPipelineMetadata guest, bool depthBound,
        out GraphicsPipelineMetadata host)
    {
        host = guest;

        var depth = guest.State.SingleOrDefault(static record => record.Kind == 21);
        var format = guest.State.SingleOrDefault(static record => record.Kind == 16);

        if (depth is not { Bytes.Length: 64 } || format is not { Bytes.Length: 8 } ||
            BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes) != 21 ||
            BinaryPrimitives.ReadUInt32LittleEndian(format.Bytes) != 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(format.Bytes.AsSpan(4)) != (depthBound ? 20U : 0U) ||
            BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(56)) != 0)
        {
            return false;
        }

        if (depthBound)
        {
            return true;
        }

        var depthEnabled = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(4));
        var writeMask = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(8));
        var comparison = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(12));
        var stencilEnabled = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(16));

        if (depthEnabled > 1 || writeMask > 1 || comparison is < 1 or > 8 || stencilEnabled != 0)
        {
            return false;
        }

        if (depthEnabled == 0 && writeMask == 0)
        {
            return true;
        }

        // Without a depth target the output-merger depth test passes and has no storage to update.
        var hostDepth = (byte[])depth.Bytes.Clone();
        hostDepth.AsSpan(4, 8).Clear();

        var state = guest.State.Select(record => record.Kind == 21
            ? new PipelineStateRecord(21, hostDepth)
            : record).ToList();

        host = guest with { State = state };

        return true;
    }
}
