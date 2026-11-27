// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Video;

/// <summary>Converts 8-bit NV12 to opaque BGRA using BT.709 limited-range coefficients.</summary>
public static class Nv12Converter
{
    public static void Convert(ReadOnlySpan<byte> lumaPlane, int yStride, ReadOnlySpan<byte> chromaPlane,
        int uvStride, int width, int height, Span<byte> bgra, int outputStride)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Even positive dimensions required.");
        }

        var rowBytes = checked(width * 4);

        Validate(lumaPlane.Length, yStride, width, height);

        Validate(chromaPlane.Length, uvStride, width, height / 2);

        Validate(bgra.Length, outputStride, rowBytes, height);

        if (lumaPlane.Overlaps(bgra) || chromaPlane.Overlaps(bgra))
        {
            throw new ArgumentException("Input and output must not overlap.");
        }

        // O(width * height) CPU fallback; the GPU presenter should avoid this conversion when its path is usable.
        for (var row = 0; row < height; row++)
        {
            var luma = lumaPlane.Slice(row * yStride, width);
            var chroma = chromaPlane.Slice(row / 2 * uvStride, width);
            var output = bgra.Slice(row * outputStride, rowBytes);

            for (var coordinateX = 0; coordinateX < width; coordinateX++)
            {
                var lumaOffset = luma[coordinateX] - 16;
                var blueChromaOffset = chroma[coordinateX & ~1] - 128;
                var redChromaOffset = chroma[(coordinateX & ~1) + 1] - 128;
                var offset = coordinateX * 4;

                output[offset] = Clamp((298 * lumaOffset + 541 * blueChromaOffset + 128) >> 8);

                output[offset + 1] =
                    Clamp((298 * lumaOffset - 55 * blueChromaOffset - 136 * redChromaOffset + 128) >> 8);

                output[offset + 2] = Clamp((298 * lumaOffset + 459 * redChromaOffset + 128) >> 8);

                output[offset + 3] = 255;
            }
        }
    }

    private static void Validate(int length, int stride, int rowBytes, int rows)
    {
        if (stride < rowBytes || (long)(rows - 1) * stride + rowBytes > length)
        {
            throw new ArgumentException("Plane extent exceeds its buffer.");
        }
    }

    private static byte Clamp(int value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }
}
