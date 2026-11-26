// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Decoding.Models;

public sealed record DecodedClip(int Width, int Height, byte[] Nv12, short[] Pcm, int SampleRate, int Channels)
{
    public int FrameBytes
    {
        get
        {
            return checked(Width * Height * 3 / 2);
        }
    }

    public int FrameCount
    {
        get
        {
            return Nv12.Length / FrameBytes;
        }
    }

    public ReadOnlySpan<byte> GetFrame(int index)
    {
        return (uint)index >= (uint)FrameCount
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : Nv12.AsSpan(index * FrameBytes, FrameBytes);
    }
}
