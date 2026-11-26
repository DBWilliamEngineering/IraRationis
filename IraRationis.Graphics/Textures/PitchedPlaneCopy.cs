// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Textures;

/// <summary>Copies active plane bytes without modifying destination row padding.</summary>
internal static class PitchedPlaneCopy
{
    internal static void Copy(ReadOnlySpan<byte> source, int sourceStride, Span<byte> destination,
        int destinationOffset, int destinationStride, int rowBytes, int rows)
    {
        if (rows <= 0 || rowBytes <= 0 || sourceStride < rowBytes ||
            destinationStride < rowBytes || destinationOffset < 0)
        {
            throw new ArgumentException("Invalid pitched plane dimensions.");
        }

        var sourceEnd = (long)(rows - 1) * sourceStride + rowBytes;
        var destinationEnd = destinationOffset + (long)(rows - 1) * destinationStride + rowBytes;

        if (sourceEnd > source.Length || destinationEnd > destination.Length)
        {
            throw new ArgumentException(
                $"Plane copy exceeds buffer: rows={rows}; rowBytes={rowBytes}; " +
                $"sourceRequired={sourceEnd}; sourceAvailable={source.Length}; " +
                $"destinationRequired={destinationEnd}; destinationAvailable={destination.Length}.");
        }

        for (var row = 0; row < rows; row++)
        {
            var sourceOffset = row * sourceStride;
            var targetOffset = destinationOffset + row * destinationStride;

            source.Slice(sourceOffset, rowBytes).CopyTo(destination.Slice(targetOffset, rowBytes));
        }
    }
}
