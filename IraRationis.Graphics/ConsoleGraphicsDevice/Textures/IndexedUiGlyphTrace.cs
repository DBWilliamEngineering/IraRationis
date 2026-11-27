// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MaximumGlyphAtlasBytes = 4 * 1024 * 1024;
    private const int MaximumGlyphCoverageReports = 12;
    private const int EarlyGlyphCoverageReports = MaximumGlyphCoverageReports / 2;
    private const int MinimumGlyphSamplePresentGap = 12;
    private const int MaximumGlyphsPerSample = 48;
    private const int MaximumGlyphExtent = 128;
    private static readonly Dictionary<IndexedUiVariant, (int Reports, long Present)> GlyphCoverageReports = new();
    private static readonly HashSet<(IndexedUiVariant Variant, int Quads)> GlyphCoverageShapes = new();

    private static void TraceIndexedUiGlyphCoverage(IndexedUiUpload upload, ReadOnlySpan<byte> indices,
        ReadOnlySpan<byte> vertices, int vertexOffset, ReadOnlySpan<byte> constants)
    {
        if (upload.Variant is not (IndexedUiVariant.AlphaMask or IndexedUiVariant.DualAlphaMask) ||
            upload.Plan.VertexStride != 20 || constants.Length < IndexedUiCompiler.VertexConstantBytes(upload.Variant)
            ||
            upload.Textures.Length == 0)
        {
            return;
        }

        var source = upload.Textures[0].Source;
        var resource = (ResourceObject*)source.Resource;
        var description = resource->Description;
        if (description.Format != HostTextureViewFormats.A8Unorm || description.DepthOrArraySize != 1 ||
            description.MipLevels != 1 || (description.Flags & 7U) != 0 || GetResourceWriterQueue(source.Resource) != 0
            ||
            !TryGetTextureView(source, out var viewFormat) || viewFormat != HostTextureViewFormats.A8Unorm ||
            (source.Description is { } view && ReadDescriptorUInt32(view, 8) != 5768) ||
            !TryPlanTextureTexture(description, out var plan) || plan is null ||
            plan.Layout.Size is 0 or > MaximumGlyphAtlasBytes ||
            plan.Layout.Planes[0] is not { Mode: 0, Bytes: 1, Mips: [var mip] } plane)
        {
            return;
        }

        var indexBytes = upload.Plan.IndexFormat == 57 ? sizeof(ushort) : sizeof(uint);
        var quadBytes = 6 * indexBytes;
        if (indices.IsEmpty || indices.Length % quadBytes != 0)
        {
            return;
        }

        var quadCount = indices.Length / quadBytes;
        var present = Interlocked.Read(ref _presentSuccesses);
        lock (IndexedUiSourceTraceGate)
        {
            var previous = GlyphCoverageReports.GetValueOrDefault(upload.Variant);
            var earlyShape = previous.Reports < EarlyGlyphCoverageReports &&
                             !GlyphCoverageShapes.Contains((upload.Variant, quadCount));
            if (previous.Reports >= MaximumGlyphCoverageReports ||
                (!earlyShape && present - previous.Present < MinimumGlyphSamplePresentGap))
            {
                return;
            }

            GlyphCoverageShapes.Add((upload.Variant, quadCount));
            GlyphCoverageReports[upload.Variant] = (previous.Reports + 1, present);
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)plan.Layout.Size);
        try
        {
            var atlas = rented.AsSpan(0, (int)plan.Layout.Size);
            if (!TryReadGuestMemory(resource->Address, atlas))
            {
                Console.Error.WriteLine(
                    $"[graphics] Indexed UI glyph coverage: presents={present}; atlas={source.Token}; snapshot=unreadable.");

                return;
            }

            var sampleCount = Math.Min(quadCount, MaximumGlyphsPerSample);
            var examined = 0;
            var empty = 0;
            var transparent = 0;
            var offscreen = 0;
            var invalidBounds = 0;
            var firstEmpty = "none";
            var rows = MemoryMarshal.Cast<byte, Vector4>(constants);
            var positionRow = upload.Variant == IndexedUiVariant.DualAlphaMask ? 4 : 2;
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var quad = sampleCount == 1 ? 0 : sample * (quadCount - 1) / (sampleCount - 1);
                if (!TryGetGlyphBounds(upload.Plan, indices.Slice(quad * quadBytes, quadBytes), indexBytes,
                        vertices, vertexOffset, rows, positionRow, out var textureBounds, out var clipCenter,
                        out var hasOpacity))
                {
                    invalidBounds++;

                    continue;
                }

                if (clipCenter.X is < -1 or > 1 || clipCenter.Y is < -1 or > 1)
                {
                    offscreen++;

                    continue;
                }

                if (!hasOpacity)
                {
                    transparent++;

                    continue;
                }

                var left = Math.Max(0, (int)(textureBounds.X * mip.Width) - 1);
                var top = Math.Max(0, (int)(textureBounds.Y * mip.Height) - 1);
                var right = Math.Min((int)mip.Width, (int)MathF.Ceiling(textureBounds.Z * mip.Width) + 1);
                var bottom = Math.Min((int)mip.Height, (int)MathF.Ceiling(textureBounds.W * mip.Height) + 1);
                var width = right - left;
                if (width is <= 0 or > MaximumGlyphExtent || bottom - top is <= 0 or > MaximumGlyphExtent)
                {
                    invalidBounds++;

                    continue;
                }

                var start = plane.Offset + mip.Offset + (ulong)top * mip.Pitch + (uint)left;
                var end = start + (ulong)(bottom - top - 1) * mip.Pitch + (uint)width;
                if (end > (ulong)atlas.Length)
                {
                    invalidBounds++;

                    continue;
                }

                examined++;
                if (HasGlyphCoverage(atlas, start, mip.Pitch, width, bottom - top))
                {
                    continue;
                }

                if (empty == 0)
                {
                    firstEmpty = FormattableString.Invariant(
                        $"quad={quad},clip={clipCenter.X:F4}/{clipCenter.Y:F4},texels={left}/{top}/{width}/{bottom - top}");
                }

                empty++;
            }

            Console.Error.WriteLine(
                $"[graphics] Indexed UI glyph coverage: presents={present}; variant={upload.Variant}; atlas={source.Token}; "
                +
                $"quads={quadCount}; sampled={sampleCount}; examined={examined}; transparent={transparent}; " +
                $"offscreen={offscreen}; invalidBounds={invalidBounds}; empty={empty}; firstEmpty={firstEmpty}.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool HasGlyphCoverage(ReadOnlySpan<byte> atlas, ulong start, uint pitch, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var offset = (int)(start + (ulong)row * pitch);
            if (atlas.Slice(offset, width).IndexOfAnyExcept((byte)0) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetGlyphBounds(IndexedUploadPlan plan, ReadOnlySpan<byte> indices, int indexBytes,
        ReadOnlySpan<byte> vertices, int vertexOffset, ReadOnlySpan<Vector4> rows, int positionRow,
        out Vector4 textureBounds,
        out Vector2 clipCenter, out bool hasOpacity)
    {
        textureBounds = default;
        clipCenter = default;
        hasOpacity = false;
        var minimum = Vector2.One;
        var maximum = Vector2.Zero;
        var positionMinimum = new Vector2(float.PositiveInfinity);
        var positionMaximum = new Vector2(float.NegativeInfinity);
        Span<uint> unique = stackalloc uint[4];
        var uniqueCount = 0;
        for (var indexOffset = 0; indexOffset < indices.Length; indexOffset += indexBytes)
        {
            var index = indexBytes == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16LittleEndian(indices[indexOffset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(indices[indexOffset..]);
            if (unique[..uniqueCount].Contains(index))
            {
                continue;
            }

            var offset = (index + plan.BaseVertex) * plan.VertexStride - vertexOffset;
            if (uniqueCount >= unique.Length || offset < 0 || offset > vertices.Length - 20)
            {
                return false;
            }

            unique[uniqueCount++] = index;
            var vertex = vertices.Slice((int)offset, 20);
            var uv = MemoryMarshal.Read<Vector2>(vertex[4..]);
            var position = MemoryMarshal.Read<Vector2>(vertex[12..]);
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X is < 0 or > 1 || uv.Y is < 0 or > 1 ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y))
            {
                return false;
            }

            hasOpacity |= MathF.FusedMultiplyAdd(rows[1].W, vertex[3] / 255.0f, rows[0].W) > 0;
            minimum = Vector2.Min(minimum, uv);
            maximum = Vector2.Max(maximum, uv);
            positionMinimum = Vector2.Min(positionMinimum, position);
            positionMaximum = Vector2.Max(positionMaximum, position);
        }

        if (uniqueCount != 4 || minimum.X >= maximum.X || minimum.Y >= maximum.Y)
        {
            return false;
        }

        var sourceCenter = (positionMinimum + positionMaximum) * 0.5f;
        clipCenter = new Vector2(TransformGlyphAxis(rows[positionRow], sourceCenter),
            TransformGlyphAxis(rows[positionRow + 1], sourceCenter));
        textureBounds = new Vector4(minimum.X, minimum.Y, maximum.X, maximum.Y);

        return float.IsFinite(clipCenter.X) && float.IsFinite(clipCenter.Y);
    }

    private static float TransformGlyphAxis(Vector4 row, Vector2 point)
    {
        return MathF.FusedMultiplyAdd(row.X, point.X, MathF.FusedMultiplyAdd(row.Y, point.Y, row.W));
    }
}
