// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using IraRationis.Graphics.Diagnostics;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static partial class ConsoleGraphicsDevice
{
    private const int IndexedUiStackSnapshotLimit = 1024;
    private static int _compactIndexedUiUploads;

    private static void StageIndexedUiUpload(IndexedUiUpload upload)
    {
        var plan = upload.Plan;
        var tryCompact =
            EnableCompactIndexedUpload && !EnableColorFlow && plan.IndexBytes is > 0 and <= MaximumCompactIndexBytes;

        var checkedIndices = tryCompact ? stackalloc byte[plan.IndexBytes] : [];
        var offset = 0;
        var bytes = 0;

        if (tryCompact)
        {
            ReadIndexedUiIndexRange(upload, checkedIndices, out offset, out bytes);

            if (TryStageCompactIndexedUiUpload(upload, checkedIndices, offset, bytes))
            {
                return;
            }
        }

        var rented = ArrayPool<byte>.Shared.Rent(plan.Capacity);

        try
        {
            var snapshot = rented.AsSpan(0, plan.Capacity);

            snapshot.Clear();

            var indices = snapshot.Slice(plan.IndexOffset, plan.IndexBytes);

            if (tryCompact)
            {
                checkedIndices.CopyTo(indices);
            }
            else
            {
                ReadIndexedUiIndexRange(upload, indices, out offset, out bytes);
            }

            var vertices = snapshot.Slice(upload.VertexOffset + offset, bytes);

            ReadIndexedUiVertexData(upload, indices, vertices, offset, snapshot);
            MapPlanarColorUpload(upload.Host, snapshot);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool TryStageCompactIndexedUiUpload(IndexedUiUpload upload, ReadOnlySpan<byte> indices, int offset,
        int bytes)
    {
        var plan = upload.Plan;
        var stagingBytes = upload.VertexOffset + bytes;

        if (bytes > MaximumCompactVertexBytes || stagingBytes + indices.Length > plan.Capacity / 2)
        {
            return false;
        }

        var rented = stagingBytes > IndexedUiStackSnapshotLimit
            ? ArrayPool<byte>.Shared.Rent(stagingBytes)
            : null;

        try
        {
            var staging = rented == null ? stackalloc byte[stagingBytes] : rented.AsSpan(0, stagingBytes);
            var constants = staging[..upload.VertexOffset];
            var vertices = staging[upload.VertexOffset..];

            constants.Clear();

            ReadIndexedUiVertexData(upload, indices, vertices, offset, constants);
            WriteCompactIndexedUpload(upload.Host, 0, plan.Capacity, 0, upload.VertexOffset + offset,
                plan.IndexOffset, constants, vertices, indices);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented, true);
            }
        }

        var observations = Interlocked.Increment(ref _compactIndexedUiUploads);

        if (GraphicsLogSampling.IsSample(observations))
        {
            Console.Error.WriteLine($"[performance] Indexed UI compact upload: observations={observations}; " +
                                    $"variant={upload.Variant}; vertexOffset={offset}; vertexBytes={bytes}; indexBytes={indices.Length}; "
                                    +
                                    $"copiedBytes={stagingBytes + indices.Length}; allocationBytes={plan.Capacity}.");
        }

        return true;
    }

    private static void ReadIndexedUiIndexRange(IndexedUiUpload upload, Span<byte> indices, out int offset,
        out int bytes)
    {
        if (!TryReadGuestMemory(upload.Plan.IndexAddress, indices))
        {
            Environment.FailFast("[graphics] indexed-ui index snapshot refused.");
        }

        if (!TryGetVertexRange(upload.Plan, indices, out offset, out bytes))
        {
            Environment.FailFast("[graphics] indexed-ui snapshot contains an index outside its vertex view.");
        }
    }

    private static void ReadIndexedUiVertexData(IndexedUiUpload upload, ReadOnlySpan<byte> indices, Span<byte> vertices,
        int offset, Span<byte> constants)
    {
        var pixelConstantBytes = IndexedUiCompiler.PixelConstantBytes(upload.Variant);
        var batchBytes = IndexedUiCompiler.BatchConstantBytes(upload.Variant);

        if (!TryReadGuestMemory(upload.Vertex + (ulong)offset, vertices) ||
            !TryReadGuestMemory(upload.VertexConstants.Address, constants[..upload.VertexConstantBytes]) ||
            (pixelConstantBytes != 0 && !TryReadGuestMemory(upload.PixelConstants.Address,
                constants.Slice(upload.PixelConstantOffset, pixelConstantBytes))))
        {
            Environment.FailFast("[graphics] indexed-ui vertex or constant snapshot refused.");
        }

        if (batchBytes != 0 &&
            !AreIndexedUiBatchesPresent(upload.Plan, indices, vertices, offset, upload.VertexConstantBytes, batchBytes))
        {
            Environment.FailFast($"[graphics] Indexed UI {upload.Variant} vertex selected constants outside its CBV.");
        }

        if (IndexedUiSourceTraceEnabled)
        {
            TraceIndexedUiGlyphCoverage(upload, indices, vertices, offset, constants);
        }
    }
}
