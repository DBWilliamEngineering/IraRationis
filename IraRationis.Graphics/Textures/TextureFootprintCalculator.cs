// SPDX-License-Identifier: GPL-3.0-or-later
// Allocation sizing adapted from Zettrox/ztrx, victory-formats/src/texture.rs.
// Based on AMD addrlib; see AMD-ADDRLIB-NOTICE.txt.

using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace IraRationis.Graphics.Textures;

internal static class TextureFootprintCalculator
{
    private const uint MaximumExtent = 16384;
    private const uint MaximumMips = 16;

    internal static uint SuggestMode(ConsoleGraphicsDevice.ResourceDescription description)
    {
        return description.Dimension == 2 ? 0U :
            (description.Flags & 2) != 0 ? 24U :
            (description.Flags & 1) != 0 ? 27U : 5U;
    }

    internal static bool TryCalculate(ConsoleGraphicsDevice.ResourceDescription description, out ulong size,
        out ulong alignment)
    {
        size = 0;
        alignment = 0;
        try
        {
            if (!TryCreate(description, out var texture))
            {
                return false;
            }

            alignment = Math.Max(texture.Alignment, description.Alignment);
            size = RoundUp(texture.Size, alignment);

            return size != 0;
        }
        catch (OverflowException)
        {
            size = 0;
            alignment = 0;

            return false;
        }
        catch (OutOfMemoryException)
        {
            size = 0;
            alignment = 0;

            return false;
        }
    }

    internal static bool TryCreate(ConsoleGraphicsDevice.ResourceDescription description,
        [NotNullWhen(true)] out TextureLayout? texture)
    {
        texture = null;
        var dimension = description.Dimension;
        var width = description.Width;
        var height = description.Height;
        var depth = (uint)description.DepthOrArraySize;
        var samples = Math.Max(1U, description.SampleCount);
        if (dimension is < 2 or > 4 || width is 0 or > MaximumExtent || height is 0 or > MaximumExtent
            || depth is 0 or > MaximumExtent ||
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
            0 => SuggestMode(description),
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
            var first = BuildPlane(dimension, (uint)width, height, depth, mips, bytes, block, samples, mode, blockLog,
                0);
            var planes = new List<TexturePlane> { first };
            if (stencil)
            {
                planes.Add(BuildPlane(dimension, (uint)width, height, depth, mips, 1, block, samples, mode, blockLog,
                    RoundUp(first.Size, first.Alignment)));
            }

            texture = new TextureLayout(description, planes.ToArray(), checked(planes[^1].Offset + planes[^1].Size),
                first.Alignment);

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static TexturePlane BuildPlane(uint dimension, uint width, uint height, uint depth, uint mips, uint bytes,
        uint block, uint samples, uint mode, int blockLog, ulong planeOffset)
    {
        var entries = new TextureMip[mips];
        var alignment = blockLog == 0 ? 256UL : 1UL << blockLog;
        var elementLog = BitOperations.Log2(bytes);
        var sampleLog = BitOperations.Log2(samples);
        var thick = dimension == 4 && mode is 5 or 9 or 17 or 21 or 25 or 26;
        uint tileWidth, tileHeight, tileDepth;
        if (blockLog == 0)
        {
            tileWidth = 256 / bytes;
            tileHeight = 1;
            tileDepth = 1;
        }
        else if (thick)
        {
            var (x, y, z) = elementLog switch
            {
                0 => (16U, 8U, 8U), 1 => (8U, 8U, 8U), 2 => (8U, 8U, 4U), 3 => (8U, 4U, 4U), _ => (4U, 4U, 4U)
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
            if (elementsLog < 0)
            {
                throw new OverflowException();
            }

            var wide = (sampleLog & 1) == 0 || (blockLog & 1) != 0;
            var w = (elementsLog + (wide ? 1 : 0)) / 2;
            tileWidth = 1U << w;
            tileHeight = 1U << (elementsLog - w);
            tileDepth = 1;
        }

        var paddedDepth = (uint)RoundUp(depth, tileDepth);
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

        var effective = thick ? blockLog - (blockLog - 8) / 3 : blockLog;
        var maxTail = effective switch { <= 8 => 0, <= 11 => 1 + (1 << (effective - 9)), _ => effective - 4 };
        var fixedWidth = tailWidth;
        var fixedHeight = tailHeight;
        if (mode == 24 && elementLog <= 1)
        {
            fixedWidth /= 2;
            if (elementLog == 0)
            {
                fixedHeight /= 2;
            }
        }

        var firstTail = mips;
        var chain = 0UL;
        for (var i = 0U; i < mips; i++)
        {
            var w = (uint)PaddedDimension(width, i, block);
            var h = (uint)PaddedDimension(height, i, block);
            if (blockLog != 0 && mips > 1 && w <= fixedWidth && h <= fixedHeight && mips - i <= (uint)maxTail)
            {
                firstTail = i;
                chain = checked(chain + alignment / tileDepth);

                break;
            }

            var pitch = (uint)RoundUp(w, tileWidth);
            var rows = (uint)RoundUp(h, tileHeight);
            var depthPitch = dimension != 4 ? 1U :
                blockLog == 0 || mips == 1 ? paddedDepth :
                thick ? (uint)RoundUp(PaddedDimension(depth, i, 1), tileDepth) : 1U;
            entries[i] = new TextureMip(Dimension(width, i, block), Dimension(height, i, block),
                dimension == 4 ? Dimension(depth, i, 1) : 1U, pitch, rows, depthPitch);
            chain = checked(chain + (ulong)pitch * rows * bytes * samples);
        }

        var offset = firstTail < mips ? alignment : 0UL;
        for (var i = (int)firstTail - 1; i >= 0; i--)
        {
            var mip = entries[i];
            entries[i] = mip with { Offset = offset };
            offset = checked(offset + (ulong)mip.Pitch * mip.Rows * bytes * samples * tileDepth);
        }

        var (microX, microY, microZ) = thick
            ? elementLog switch
            {
                0 => (8U, 4U, 8U), 1 => (4U, 4U, 8U), 2 => (4U, 4U, 4U), 3 => (4U, 2U, 4U), _ => (2U, 2U, 4U)
            }
            : elementLog switch
            {
                0 => (16U, 16U, 1U), 1 => (16U, 8U, 1U), 2 => (8U, 8U, 1U), 3 => (8U, 4U, 1U), _ => (4U, 4U, 1U)
            };
        var tailPitch = tailWidth;
        var tailRows = tailHeight;
        for (var i = firstTail; i < mips; i++)
        {
            var m = maxTail - 1 - (int)(i - firstTail);
            var tailOffset = (uint)(m > 6 ? 16 << m : m << 8);
            uint x = 0, y = 0;
            for (var bit = 0; bit < 6; bit++)
            {
                x |= ((tailOffset >> (9 + 2 * bit)) & 1) << bit;
                y |= ((tailOffset >> (8 + 2 * bit)) & 1) << bit;
            }

            if ((blockLog & 1) != 0)
            {
                (x, y) = (y, x);
                if ((elementLog & 1) != 0)
                {
                    y = (y << 1) | (x & 1);
                    x >>= 1;
                }
            }

            entries[i] = new TextureMip(Dimension(width, i, block), Dimension(height, i, block),
                dimension == 4 ? Dimension(depth, i, 1) : 1U, tailPitch, tailRows,
                thick ? (uint)RoundUp(PaddedDimension(depth, i, 1), microZ) : 1U, 0, true, tailOffset, x * microX,
                y * microY);
            tailPitch = Math.Max(tailPitch >> 1, microX);
            tailRows = Math.Max(tailRows >> 1, microY);
        }

        var sliceSize = checked(chain * tileDepth);

        return new TexturePlane(planeOffset, checked(chain * paddedDepth), alignment, sliceSize, bytes, block, samples,
            mode, tileWidth, tileHeight, tileDepth, paddedDepth / tileDepth, thick, firstTail, entries);
    }

    private static uint Dimension(uint extent, uint mip, uint block)
    {
        return (Math.Max(1U, extent >> (int)mip) + block - 1) / block;
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
