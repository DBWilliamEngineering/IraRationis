// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Drawing.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static partial class ConsoleGraphicsDevice
{
    private const int MaximumIndexedUpload = 8 * 1024 * 1024;

    private static bool TryPlanIndexedUpload(RecordedBufferView vertex, RecordedBufferView index,
        uint count, uint first, int baseVertex, [NotNullWhen(true)] out IndexedUploadPlan? plan)
    {
        plan = null;

        var indexSize = index.FormatOrStride switch
        {
            57U => 2UL,
            42U => 4UL,
            _ => 0UL
        };

        if (indexSize == 0 || count == 0 || vertex.FormatOrStride is 0 or > 256 ||
            vertex.Size < vertex.FormatOrStride || vertex.Size > MaximumIndexedUpload ||
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

        var offset = ((ulong)RectangleVertexOffset + vertex.Size + 3UL) & ~3UL;
        var capacity = offset + bytes;

        if (capacity > MaximumIndexedUpload)
        {
            return false;
        }

        plan = new IndexedUploadPlan(index.Address + start, index.FormatOrStride, (int)vertex.Size,
            vertex.FormatOrStride, (int)offset, (int)bytes, (int)capacity, baseVertex);

        return true;
    }

    private static bool ValidateIndices(IndexedUploadPlan plan, ReadOnlySpan<byte> indices)
    {
        return TryGetVertexRange(plan, indices, out _, out _);
    }

    // Preserve the original vertex-buffer origin and base-vertex arithmetic.
    // The range is relative to the original vertex view, not the selected indices.
    private static bool TryGetVertexRange(IndexedUploadPlan plan, ReadOnlySpan<byte> indices,
        out int byteOffset, out int byteCount)
    {
        byteOffset = 0;

        byteCount = 0;

        var size = plan.IndexFormat switch
        {
            57U => 2,
            42U => 4,
            _ => 0
        };

        if (size == 0 || plan.VertexStride == 0 || plan.VertexBytes <= 0 || indices.IsEmpty ||
            indices.Length != plan.IndexBytes || indices.Length % size != 0)
        {
            return false;
        }

        var vertexCount = plan.VertexBytes / plan.VertexStride;
        var minimum = long.MaxValue;
        var maximum = -1L;

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

            minimum = Math.Min(minimum, effective);

            maximum = Math.Max(maximum, effective);
        }

        byteOffset = checked((int)(minimum * plan.VertexStride));

        byteCount = checked((int)((maximum - minimum + 1) * plan.VertexStride));

        return byteCount > 0 && byteOffset <= plan.VertexBytes - byteCount;
    }

    private static void VerifyIndexedUpload()
    {
        var vertex = new RecordedBufferView { Address = 0x1000, Size = 96, FormatOrStride = 24 };
        var index = new RecordedBufferView { Address = 0x2000, Size = 16, FormatOrStride = 57 };

        if (!TryPlanIndexedUpload(vertex, index, 3, 2, -1, out var plan) ||
            plan.IndexAddress != 0x2004 || plan.IndexBytes != 6 || plan.IndexOffset != 352 ||
            !ValidateIndices(plan, [1, 0, 2, 0, 4, 0]) ||
            ValidateIndices(plan, [0, 0, 2, 0, 4, 0]) ||
            ValidateIndices(plan, [1, 0, 2, 0, 5, 0]) ||
            TryPlanIndexedUpload(vertex, index, 3, 7, 0, out _))
        {
            throw new InvalidOperationException("R20 16-bit indexed staging regression failed.");
        }

        index.FormatOrStride = 42;

        if (!TryPlanIndexedUpload(vertex, index, 3, 1, 0, out var wide) ||
            wide.IndexAddress != 0x2004 || wide.IndexBytes != 12 ||
            !ValidateIndices(wide, [0, 0, 0, 0, 1, 0, 0, 0, 3, 0, 0, 0]) ||
            ValidateIndices(wide, [0, 0, 0, 0, 1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff]))
        {
            throw new InvalidOperationException("R20 32-bit indexed staging regression failed.");
        }

        var narrow = new IndexedUploadPlan(0x2000, 57, 1024, 16, 1280, 6, 1286, -1);

        if (!TryGetVertexRange(narrow, [3, 0, 5, 0, 4, 0], out var offset, out var count) ||
            offset != 32 || count != 48 ||
            TryGetVertexRange(narrow, [0, 0, 5, 0, 4, 0], out _, out _) ||
            !TryGetVertexRange(wide, [1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0], out offset, out count) ||
            offset != 24 || count != 48)
        {
            throw new InvalidOperationException("R26 referenced vertex range regression failed.");
        }

        Console.Error.WriteLine(
            "[graphics-probe] R20/R26 indexed staging bounds, range and base-vertex tests passed (CPU only).");
    }
}
