// SPDX-License-Identifier: GPL-3.0-or-later
// Allocation sizing adapted from Zettrox/ztrx, victory-formats/src/texture.rs.
// Based on AMD addrlib; see AMD-ADDRLIB-NOTICE.txt.
using System.Numerics;

namespace IraRationis.Graphics.Textures;

internal static class TextureFootprintCalculator
{
    private const uint MaximumExtent = 16384;
    private const uint MaximumMips = 16;

    internal static bool TryCalculate(ConsoleGraphicsDevice.ResourceDescription description, out ulong size, out ulong alignment)
    {
        size = 0;
        alignment = 0;
        var dimension = description.Dimension;
        var width = description.Width;
        var height = description.Height;
        var depth = (uint)description.DepthOrArraySize;
        var samples = Math.Max(1U, description.SampleCount);
        if (dimension is < 2 or > 4 || width is 0 or > MaximumExtent || height is 0 or > MaximumExtent || depth is 0 or > MaximumExtent ||
            !BitOperations.IsPow2(samples) || samples > 8 || (dimension == 2 && height != 1) ||
            (description.Alignment != 0 && !BitOperations.IsPow2(description.Alignment)))
        {
            return false;
        }
        var mips = description.MipLevels == 0
            ? (uint)(BitOperations.Log2(Math.Max((uint)width, Math.Max(height, dimension == 4 ? depth : 1U))) + 1)
            : description.MipLevels;
        if (mips > MaximumMips || (samples != 1 && (dimension != 3 || mips != 1)))
        {
            return false;
        }
        var mode = description.Layout switch
        {
            0 when dimension == 2 => 0U,
            0 when (description.Flags & 2) != 0 => 24U,
            0 when (description.Flags & 1) != 0 => 27U,
            0 => 5U,
            1 => 0U,
            >= 0x100 and <= 0x1FF => description.Layout & 0xFF,
            _ => uint.MaxValue
        };
        var blockLog = mode switch
        {
            0 => 0,
            1 or 2 => 8,
            5 or 6 or >= 21 and <= 23 => 12,
            9 or 10 or 17 or 18 or >= 24 and <= 27 => 16,
            _ => -1
        };
        if (blockLog < 0 || (dimension == 4 && mode is not (0 or 5 or 9 or 17 or 21 or >= 24 and <= 27)))
        {
            return false;
        }
        var (bytes, block, stencil) = description.Format switch
        {
            >= 1 and <= 4 => (16U, 1U, false),
            >= 9 and <= 18 => (8U, 1U, false),
            >= 19 and <= 22 or >= 44 and <= 47 => (4U, 1U, true),
            >= 23 and <= 43 or 67 or >= 87 and <= 93 or 116 or 117 => (4U, 1U, false),
            >= 48 and <= 59 or 85 or 86 or 115 => (2U, 1U, false),
            >= 118 and <= 120 => (2U, 1U, true),
            >= 60 and <= 65 => (1U, 1U, false),
            >= 70 and <= 72 or >= 79 and <= 81 => (8U, 4U, false),
            >= 73 and <= 78 or >= 82 and <= 84 or >= 94 and <= 99 => (16U, 4U, false),
            _ => (0U, 0U, false)
        };
        // Unsupported formats are not assigned guessed footprints. Linear MSAA
        // is not supported by this port (upstream linear sizing omits samples).
        if (bytes == 0 || (blockLog == 0 && samples != 1))
        {
            return false;
        }
        try
        {
            var natural = blockLog == 0 ? 256UL : 1UL << blockLog;
            var total = PlaneSize(dimension, (uint)width, height, depth, mips, bytes, block, samples, mode, blockLog);
            if (stencil)
            {
                total = checked(RoundUp(total, natural) + PlaneSize(dimension, (uint)width, height, depth, mips, 1, block, samples, mode, blockLog));
            }
            var required = Math.Max(natural, description.Alignment);
            size = RoundUp(total, required);
            alignment = required;
            return size != 0;
        }
        catch (OverflowException)
        {
            size = 0;
            alignment = 0;
            return false;
        }
    }

    private static ulong PlaneSize(uint dimension, uint width, uint height, uint depth, uint mips, uint bytes, uint block, uint samples, uint mode, int blockLog)
    {
        var chain = 0UL;
        if (blockLog == 0)
        {
            for (var mip = 0U; mip < mips; mip++)
            {
                var pitch = RoundUp(PaddedDimension(width, mip, block), 256 / bytes);
                chain = checked(chain + pitch * PaddedDimension(height, mip, block) * bytes);
            }
            return checked(chain * depth);
        }
        var elementLog = BitOperations.Log2(bytes);
        var sampleLog = BitOperations.Log2(samples);
        var blockBytes = 1UL << blockLog;
        var thick = dimension == 4 && mode is (5 or 9 or 17 or 21 or 25 or 26);
        uint tileWidth, tileHeight, tileDepth;
        if (thick)
        {
            var (x, y, z) = elementLog switch
            {
                0 => (16U, 8U, 8U),
                1 => (8U, 8U, 8U),
                2 => (8U, 8U, 4U),
                3 => (8U, 4U, 4U),
                _ => (4U, 4U, 4U)
            };
            var amp = (blockLog - 10) / 3;
            var rest = (blockLog - 10) % 3;
            tileWidth = x << amp;
            tileHeight = y << (amp + rest / 2);
            tileDepth = z << (amp + (rest != 0 ? 1 : 0));
        }
        else
        {
            var elementsLog = blockLog - elementLog - sampleLog;
            var wide = (sampleLog & 1) == 0 || (blockLog & 1) != 0;
            var w = (elementsLog + (wide ? 1 : 0)) / 2;
            tileWidth = 1U << w;
            tileHeight = 1U << (elementsLog - w);
            tileDepth = 1;
        }
        var slices = RoundUp(depth, tileDepth);
        if (mips == 1)
        {
            return checked(RoundUp(PaddedDimension(width, 0, block), tileWidth) *
                RoundUp(PaddedDimension(height, 0, block), tileHeight) * bytes * samples * slices);
        }
        var tailWidth = tileWidth;
        var tailHeight = tileHeight;
        if (!thick || blockLog % 3 == 1)
        {
            tailWidth >>= 1;
        }
        else if (blockLog % 3 == 0)
        {
            tailHeight >>= 1;
        }
        // When thick blockLog % 3 == 2, upstream halves the tail depth,
        // which is not part of its width/height tail-entry test.
        var effective = thick ? blockLog - (blockLog - 8) / 3 : blockLog;
        var maxTail = effective switch
        {
            8 => 0,
            >= 9 and <= 11 => 1 + (1 << (effective - 9)),
            _ => effective - 4
        };
        if (mode == 24 && elementLog <= 1)
        {
            tailWidth /= 2;
            if (elementLog == 0)
            {
                tailHeight /= 2;
            }
        }
        for (var mip = 0U; mip < mips; mip++)
        {
            var w = PaddedDimension(width, mip, block);
            var h = PaddedDimension(height, mip, block);
            if (w <= tailWidth && h <= tailHeight && mips - mip <= (uint)maxTail)
            {
                chain = checked(chain + blockBytes / tileDepth);
                break;
            }
            chain = checked(chain + RoundUp(w, tileWidth) * RoundUp(h, tileHeight) * bytes);
        }
        return checked(chain * slices);
    }

    private static ulong PaddedDimension(uint extent, uint mip, uint block)
    {
        var elements = ((ulong)extent + block - 1) / block;
        var divisor = 1UL << (int)mip;
        return (elements + divisor - 1) / divisor;
    }

    private static ulong RoundUp(ulong value, ulong alignment)
    {
        return checked(value + alignment - 1) & ~(alignment - 1);
    }
}
