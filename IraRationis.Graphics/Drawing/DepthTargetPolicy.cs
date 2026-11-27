// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics.Shaders.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.Drawing;

internal static class DepthTargetPolicy
{
    private const uint DepthStencilStateKind = 21;
    private const int DepthStencilRecordBytes = 64;
    private const int DepthEnableOffset = 4;
    private const int DepthWriteMaskOffset = 8;
    private const float DefaultMinimumDepth = 0.0f;
    private const float DefaultMaximumDepth = 1.0f;
    private const float DepthBoundsEndpointTolerance = 1.0e-7f;

    internal static bool IsEffectivelyDefaultBounds(float minimum, float maximum)
    {
        return minimum >= DefaultMinimumDepth && minimum - DefaultMinimumDepth <= DepthBoundsEndpointTolerance &&
               maximum <= DefaultMaximumDepth && DefaultMaximumDepth - maximum <= DepthBoundsEndpointTolerance;
    }

    internal static bool WritesDepth(GraphicsPipelineMetadata pipeline)
    {
        var depth = pipeline.State.SingleOrDefault(static record => record.Kind == DepthStencilStateKind);

        return depth is { Bytes.Length: DepthStencilRecordBytes } &&
               BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(DepthEnableOffset)) != 0 &&
               BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(DepthWriteMaskOffset)) ==
               (uint)D3D12_DEPTH_WRITE_MASK.D3D12_DEPTH_WRITE_MASK_ALL;
    }

    internal static bool TryGetHostState(GraphicsPipelineMetadata guest, bool depthBound,
        out GraphicsPipelineMetadata host)
    {
        host = guest;

        var depth = guest.State.SingleOrDefault(static record => record.Kind == DepthStencilStateKind);
        var format = guest.State.SingleOrDefault(static record => record.Kind == 16);

        if (depth is not { Bytes.Length: DepthStencilRecordBytes } || format is not { Bytes.Length: 8 } ||
            BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes) != DepthStencilStateKind ||
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

        var depthEnabled = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(DepthEnableOffset));
        var writeMask = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(DepthWriteMaskOffset));
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

        hostDepth.AsSpan(DepthEnableOffset, 2 * sizeof(uint)).Clear();

        var state = guest.State.Select(record => record.Kind == DepthStencilStateKind
            ? new PipelineStateRecord(DepthStencilStateKind, hostDepth)
            : record).ToList();

        host = guest with { State = state };

        return true;
    }
}
