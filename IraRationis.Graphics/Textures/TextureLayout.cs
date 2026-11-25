// SPDX-License-Identifier: GPL-3.0-or-later
// Layout and addressing adapted from Zettrox/ztrx victory-formats/src/texture.rs.
// AMD addrlib attribution: AMD-ADDRLIB-NOTICE.txt.

using System.Numerics;

namespace IraRationis.Graphics.Textures;

internal readonly record struct TextureMip(
    uint Width,
    uint Height,
    uint Depth,
    uint Pitch,
    uint Rows,
    uint DepthPitch,
    ulong Offset = 0,
    bool InTail = false,
    uint TailOffset = 0,
    uint TailX = 0,
    uint TailY = 0);

internal sealed record TextureLayout(
    ConsoleGraphicsDevice.ResourceDescription Description,
    TexturePlane[] Planes,
    ulong Size,
    ulong Alignment);

internal sealed record TexturePlane(
    ulong Offset,
    ulong Size,
    ulong Alignment,
    ulong SliceSize,
    uint Bytes,
    uint Block,
    uint Samples,
    uint Mode,
    uint TileWidth,
    uint TileHeight,
    uint TileDepth,
    uint Slices,
    bool Thick,
    uint FirstTail,
    TextureMip[] Mips)
{
    private static readonly string[][] Micro =
    [
        ["x0x1x2x3y0y1y2y3", "--x0x1x2y0y1y2x3", "----x0x1y0y1y2x2", "------x0y0y1x1x2", "--------y0y1x0x1"],
        ["x0x1x2y1y0y2x3y3", "--x0x1x2y0y1y2x3", "----x0x1y0y1x2y2", "------x0y0x1x2y1", "--------x0y0x1y1"],
        ["x0x1z0y0z1y1x2z2", "--x0z0y0z1y1x1z2", "----x0y0z0y1x1z1", "------x0z0y0x1z1", "--------z0y0x0z1"]
    ];

    private uint[][]? _patterns;

    internal bool Addressable
    {
        get
        {
            return Samples == 1 && Mode < 16;
        }
    }

    internal uint[][] Patterns()
    {
        var existing = Volatile.Read(ref _patterns);
        if (existing != null)
        {
            return existing;
        }

        uint[][] generated = Mode == 0
            ? [[], [], []]
            : [Spread('x', TileWidth), Spread('y', TileHeight), Spread('z', TileDepth)];

        return Interlocked.CompareExchange(ref _patterns, generated, null) ?? generated;
    }

    private uint[] Spread(char axis, uint extent)
    {
        var values = new uint[extent];
        var kind = Thick ? 2 : Mode is 2 or 6 or 10 ? 1 : 0;
        var pattern = Micro[kind][BitOperations.Log2(Bytes)];
        var order = Thick ? "yxz" : "yx";
        var index = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            if (pattern[bit * 2] != axis)
            {
                continue;
            }

            var source = pattern[bit * 2 + 1] - '0';
            index = Math.Max(index, source + 1);
            for (var v = 0; v < values.Length; v++)
            {
                values[v] |= (((uint)v >> source) & 1U) << bit;
            }
        }

        for (var bit = 8; bit < BitOperations.Log2(Alignment); bit++)
        {
            if (order[(bit - 8) % order.Length] != axis)
            {
                continue;
            }

            for (var v = 0; v < values.Length; v++)
            {
                values[v] |= (((uint)v >> index) & 1U) << bit;
            }

            index++;
        }

        return values;
    }
}
