namespace IraRationis.Media;

/// <summary>Converts 8-bit NV12 to opaque BGRA using BT.709 limited-range coefficients.</summary>
public static class Nv12Converter
{
    public static void Convert(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> uv,
        int uvStride, int width, int height, Span<byte> bgra, int outputStride)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Even positive dimensions required.");
        }

        var rowBytes = checked(width * 4);
        Validate(y.Length, yStride, width, height);
        Validate(uv.Length, uvStride, width, height / 2);
        Validate(bgra.Length, outputStride, rowBytes, height);
        if (y.Overlaps(bgra) || uv.Overlaps(bgra))
        {
            throw new ArgumentException("Input and output must not overlap.");
        }

        for (var row = 0; row < height; row++)
        {
            var luma = y.Slice(row * yStride, width);
            var chroma = uv.Slice(row / 2 * uvStride, width);
            var output = bgra.Slice(row * outputStride, rowBytes);
            for (var x = 0; x < width; x++)
            {
                var c = luma[x] - 16;
                var d = chroma[x & ~1] - 128;
                var e = chroma[(x & ~1) + 1] - 128;
                var offset = x * 4;
                output[offset] = Clamp((298 * c + 541 * d + 128) >> 8);
                output[offset + 1] = Clamp((298 * c - 55 * d - 136 * e + 128) >> 8);
                output[offset + 2] = Clamp((298 * c + 459 * e + 128) >> 8);
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
