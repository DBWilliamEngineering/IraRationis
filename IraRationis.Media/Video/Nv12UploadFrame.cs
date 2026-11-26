// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Video;

/// <summary>Owns a CPU snapshot of two NV12 planes. No native resource is owned by this type.</summary>
public sealed class Nv12UploadFrame
{
    private readonly byte[] _pixels;

    private Nv12UploadFrame(int width, int height, long timestamp, byte[] pixels)
    {
        Width = width;

        Height = height;

        Timestamp = timestamp;

        _pixels = pixels;
    }

    public int Width
    {
        get;
    }

    public int Height
    {
        get;
    }

    public long Timestamp
    {
        get;
    }

    public static Nv12UploadFrame Capture(ReadOnlySpan<byte> luma, int lumaStride,
        ReadOnlySpan<byte> chroma, int chromaStride, int width, int height, long timestamp)
    {
        if (width is < 2 or > 3840 || height is < 2 or > 2160 ||
            (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Even positive NV12 dimensions required.");
        }

        ValidatePlane(luma.Length, lumaStride, width, height);

        ValidatePlane(chroma.Length, chromaStride, width, height / 2);

        var yBytes = checked(width * height);
        var pixels = new byte[checked(yBytes + yBytes / 2)];

        CopyRows(luma, lumaStride, pixels.AsSpan(0, yBytes), width, width, height);

        CopyRows(chroma, chromaStride, pixels.AsSpan(yBytes), width, width, height / 2);

        return new Nv12UploadFrame(width, height, timestamp, pixels);
    }

    /// <summary>Copies into independently supplied upload footprints. The caller owns native submission and fences.</summary>
    public void CopyToUpload(Span<byte> luma, int lumaPitch, Span<byte> chroma, int chromaPitch)
    {
        if ((lumaPitch & 255) != 0 || (chromaPitch & 255) != 0 || luma.Overlaps(chroma))
        {
            throw new ArgumentException("Distinct, 256-byte-aligned row pitches required.");
        }

        var yLength = checked(lumaPitch * Height);
        var uvLength = checked(chromaPitch * (Height / 2));

        if (lumaPitch < Width || chromaPitch < Width || luma.Length < yLength || chroma.Length < uvLength)
        {
            throw new ArgumentException("Upload footprint is too small.");
        }

        var yBytes = Width * Height;
        var lumaPlane = luma[..yLength];
        var chromaPlane = chroma[..uvLength];

        lumaPlane.Clear();

        chromaPlane.Clear();

        CopyRows(_pixels.AsSpan(0, yBytes), Width, lumaPlane, lumaPitch, Width, Height);

        CopyRows(_pixels.AsSpan(yBytes), Width, chromaPlane, chromaPitch, Width, Height / 2);
    }

    public void ConvertToBgra(Span<byte> destination, int stride)
    {
        var yBytes = Width * Height;

        Nv12Converter.Convert(_pixels.AsSpan(0, yBytes), Width, _pixels.AsSpan(yBytes), Width,
            Width, Height, destination, stride);
    }

    private static void ValidatePlane(int length, int stride, int width, int rows)
    {
        if (stride < width || (long)(rows - 1) * stride + width > length)
        {
            throw new ArgumentException("NV12 source plane is too small.");
        }
    }

    private static void CopyRows(ReadOnlySpan<byte> source, int sourceStride, Span<byte> destination,
        int destinationStride, int width, int rows)
    {
        for (var row = 0; row < rows; row++)
        {
            source.Slice(row * sourceStride, width).CopyTo(destination.Slice(row * destinationStride, width));
        }
    }
}
