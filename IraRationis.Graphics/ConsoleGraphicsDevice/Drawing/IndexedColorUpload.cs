// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using IraRationis.Graphics.Diagnostics;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MaximumCompactIndexBytes = 8 * 1024;
    private const int MaximumCompactVertexBytes = 64 * 1024;
    private static readonly bool EnableCompactIndexedUpload =
        Environment.GetEnvironmentVariable("IRX_COMPACT_INDEX_UPLOAD") != "0";
    private static int _compactIndexedColorUploads;

    private static bool TryPrepareCompactIndexedColorUpload(RectangleUpload upload, IndexedUploadPlan plan)
    {
        if (plan.IndexBytes is <= 0 or > MaximumCompactIndexBytes)
        {
            return false;
        }

        Span<byte> indices = stackalloc byte[plan.IndexBytes];

        if (!TryReadGuestMemory(plan.IndexAddress, indices))
        {
            Environment.FailFast("[graphics] Indexed color compact staging could not read the guest indices.");
        }

        if (!TryGetVertexRange(plan, indices, out var vertexOffset, out var vertexBytes))
        {
            Environment.FailFast("[graphics] Indexed color compact staging found an index outside the guest vertex view.");
        }

        if (vertexBytes > MaximumCompactVertexBytes || vertexBytes >= plan.VertexBytes)
        {
            return false;
        }

        var stagingBytes = vertexBytes + upload.ConstantBytes;
        var rented = stagingBytes > RectangleStackSnapshotLimit ? ArrayPool<byte>.Shared.Rent(stagingBytes) : null;

        try
        {
            var staging = rented == null ? stackalloc byte[stagingBytes] : rented.AsSpan(0, stagingBytes);
            var vertices = staging[..vertexBytes];
            var constants = staging[vertexBytes..];
            Span<byte> token = stackalloc byte[16];
            Span<byte> recheck = stackalloc byte[16];

            if (!TryReadGuestMemory(upload.Table, token) || BinaryPrimitives.ReadUInt32LittleEndian(token) != ConstantTag ||
                !TryReadGuestMemory(upload.Vertex + (ulong)vertexOffset, vertices))
            {
                Environment.FailFast("[graphics] Indexed color compact staging could not read the guest descriptor or vertices.");
            }

            var requiredBytes = upload.ConstantBytes;

            if (upload.IndexedArray && !TryConstantExtent(plan, vertices, indices, out requiredBytes, vertexOffset))
            {
                Environment.FailFast("[graphics] Indexed color compact staging could not determine the constant extent.");
            }

            var address = BinaryPrimitives.ReadUInt64LittleEndian(token[8..]);
            var declaredBytes = BinaryPrimitives.ReadUInt32LittleEndian(token[4..]);

            if (address == 0 || requiredBytes > constants.Length || declaredBytes < (uint)requiredBytes ||
                address > ulong.MaxValue - (ulong)requiredBytes ||
                !TryReadGuestMemory(address, constants[..requiredBytes]) ||
                !TryReadGuestMemory(upload.Table, recheck) || !token.SequenceEqual(recheck))
            {
                Environment.FailFast("[graphics] Indexed color compact staging found unreadable or changed constants.");
            }

            constants[requiredBytes..].Clear();
            WriteCompactIndexedUpload(upload.Slice.Host, upload.Slice.Offset, upload.Capacity, upload.ConstantOffset,
                RectangleVertexOffset + vertexOffset, plan.IndexOffset, constants, vertices, indices);
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented, true);
            }
        }

        var count = Interlocked.Increment(ref _compactIndexedColorUploads);

        if (GraphicsLogSampling.IsSample(count))
        {
            Console.Error.WriteLine($"[performance] Indexed color compact upload: observations={count}; " +
                $"vertexOffset={vertexOffset}; vertexBytes={vertexBytes}; indexBytes={plan.IndexBytes}; " +
                $"constantBytes={upload.ConstantBytes}; copiedBytes={stagingBytes + plan.IndexBytes}; allocationBytes={upload.Capacity}.");
        }

        return true;
    }

    private static void WriteCompactIndexedUpload(nint host, int hostOffset, int capacity, int constantDestination,
        int vertexDestination, int indexDestination, ReadOnlySpan<byte> constants, ReadOnlySpan<byte> vertices,
        ReadOnlySpan<byte> indices)
    {
        var noRead = default(HostRange);
        var mapped = (void*)null;
        var result = ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(host, 8))
            (host, 0, &noRead, &mapped);

        if (result < 0 || mapped == null)
        {
            Environment.FailFast($"[graphics] Compact indexed upload Map HRESULT=0x{result:X8}.");
        }

        var written = default(HostRange);

        try
        {
            // Retain the original input-assembler addresses and base vertex.
            var destination = new Span<byte>((byte*)mapped + hostOffset, capacity);
            vertices.CopyTo(destination.Slice(vertexDestination, vertices.Length));
            indices.CopyTo(destination.Slice(indexDestination, indices.Length));
            constants.CopyTo(destination.Slice(constantDestination, constants.Length));

            written.Begin = (nuint)(hostOffset + Math.Min(constantDestination, Math.Min(vertexDestination, indexDestination)));
            written.End = (nuint)(hostOffset + Math.Max(constantDestination + constants.Length,
                Math.Max(vertexDestination + vertices.Length, indexDestination + indices.Length)));
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(host, 9))(host, 0, &written);
        }
    }
}
