// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    private const int R20MaximumIndexedUpload = 8 * 1024 * 1024;

    private static bool TryPlanR20IndexedUpload(RecordedBufferView vertex, RecordedBufferView index,
        uint count, uint first, int baseVertex, [NotNullWhen(true)] out R20IndexedUploadPlan? plan)
    {
        plan = null;
        var indexSize = index.FormatOrStride switch { 57U => 2UL, 42U => 4UL, _ => 0UL };
        if (indexSize == 0 || count == 0 || vertex.FormatOrStride is 0 or > 256 ||
            vertex.Size < vertex.FormatOrStride || vertex.Size > R20MaximumIndexedUpload ||
            vertex.Address == 0 || index.Address == 0 || index.Address % indexSize != 0 ||
            vertex.Address > ulong.MaxValue - vertex.Size)
        {
            return false;
        }

        var start = first * indexSize;
        var bytes = count * indexSize;
        if (start > index.Size || bytes > index.Size - start ||
            start > ulong.MaxValue - index.Address || bytes > ulong.MaxValue - (index.Address + start))
        {
            return false;
        }

        var offset = ((ulong)R14VertexOffset + vertex.Size + 3UL) & ~3UL;
        var capacity = offset + bytes;
        if (capacity > R20MaximumIndexedUpload)
        {
            return false;
        }

        plan = new R20IndexedUploadPlan(index.Address + start, index.FormatOrStride, (int)vertex.Size,
            vertex.FormatOrStride, (int)offset, (int)bytes, (int)capacity, baseVertex);

        return true;
    }

    private static bool ValidateR20Indices(R20IndexedUploadPlan plan, ReadOnlySpan<byte> indices)
    {
        var size = plan.IndexFormat switch { 57U => 2, 42U => 4, _ => 0 };
        if (size == 0 || plan.VertexStride == 0 || indices.Length != plan.IndexBytes || indices.Length % size != 0)
        {
            return false;
        }

        var vertexCount = plan.VertexBytes / plan.VertexStride;
        for (var offset = 0; offset < indices.Length; offset += size)
        {
            var raw = size == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(indices[offset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(indices[offset..]);
            var effective = raw + plan.BaseVertex;
            if (effective < 0 || effective >= vertexCount)
            {
                return false;
            }
        }

        return true;
    }

    private static void VerifyR20IndexedUpload()
    {
        var vertex = new RecordedBufferView { Address = 0x1000, Size = 96, FormatOrStride = 24 };
        var index = new RecordedBufferView { Address = 0x2000, Size = 16, FormatOrStride = 57 };
        if (!TryPlanR20IndexedUpload(vertex, index, 3, 2, -1, out var plan) ||
            plan.IndexAddress != 0x2004 || plan.IndexBytes != 6 || plan.IndexOffset != 352 ||
            !ValidateR20Indices(plan, [1, 0, 2, 0, 4, 0]) ||
            ValidateR20Indices(plan, [0, 0, 2, 0, 4, 0]) ||
            ValidateR20Indices(plan, [1, 0, 2, 0, 5, 0]) ||
            TryPlanR20IndexedUpload(vertex, index, 3, 7, 0, out _))
        {
            throw new InvalidOperationException("R20 16-bit indexed staging regression failed.");
        }

        index.FormatOrStride = 42;
        if (!TryPlanR20IndexedUpload(vertex, index, 3, 1, 0, out var wide) ||
            wide.IndexAddress != 0x2004 || wide.IndexBytes != 12 ||
            !ValidateR20Indices(wide, [0, 0, 0, 0, 1, 0, 0, 0, 3, 0, 0, 0]) ||
            ValidateR20Indices(wide, [0, 0, 0, 0, 1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff]))
        {
            throw new InvalidOperationException("R20 32-bit indexed staging regression failed.");
        }

        Console.Error.WriteLine("[graphics-probe] R20 indexed staging bounds and base-vertex tests passed (CPU only).");
    }

    // The vertex view is kept at its original origin so signed base-vertex
    // arithmetic and SV_VertexID remain correct. Only the selected index range
    // is staged. The caller must establish CPU authority before uploading.
    private sealed record R20IndexedUploadPlan(
        ulong IndexAddress,
        uint IndexFormat,
        int VertexBytes,
        uint VertexStride,
        int IndexOffset,
        int IndexBytes,
        int Capacity,
        int BaseVertex);
}
