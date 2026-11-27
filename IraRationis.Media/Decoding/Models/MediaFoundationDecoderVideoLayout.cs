// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Decoding.Models;

internal readonly record struct MediaFoundationDecoderVideoLayout(
    int CodedWidth,
    int CodedHeight,
    int X,
    int Y,
    int Width,
    int Height)
{
    public int CodedBytes
    {
        get
        {
            return checked(CodedWidth * CodedHeight * 3 / 2);
        }
    }

    public int VisibleBytes
    {
        get
        {
            return checked(Width * Height * 3 / 2);
        }
    }

    public bool IsFullFrame
    {
        get
        {
            return X == 0 && Y == 0 && Width == CodedWidth && Height == CodedHeight;
        }
    }
}
