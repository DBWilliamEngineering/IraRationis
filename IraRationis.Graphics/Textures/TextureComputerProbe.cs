// SPDX-License-Identifier: GPL-3.0-or-later
// Synthetic tests adapted from upstream victory-hle/src/modules/xg.rs.

namespace IraRationis.Graphics.Textures;

internal static unsafe class TextureComputerProbe
{
    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"XG probe failed: {name}.");
        }
    }

    private static nint Slot(nint self, int slot)
    {
        return (*(nint**)self)[slot];
    }

    internal static void Verify()
    {
        Check(sizeof(ConsoleGraphicsDevice.ResourceDescription) == 56, "resource description ABI");

        var suggest =
            (delegate* unmanaged[Stdcall]<ConsoleGraphicsDevice.ResourceDescription*, uint, uint*, int>)TextureComputer
                .SuggestAddress;
        var create =
            (delegate* unmanaged[Stdcall]<ConsoleGraphicsDevice.ResourceDescription*, nint*, int>)TextureComputer
                .CreateAddress;
        var desc = new ConsoleGraphicsDevice.ResourceDescription
        {
            Dimension = 3,
            Width = 3840,
            Height = 2160,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 28,
            SampleCount = 1,
            Flags = 0x300005
        };

        uint mode = 0;

        Check(suggest(&desc, 0, &mode) == 0 && mode == 0x11B, "render suggestion");

        desc.Flags = 2;

        Check(suggest(&desc, 0, &mode) == 0 && mode == 0x118, "depth suggestion");

        desc.Flags = 0;

        Check(suggest(&desc, 0, &mode) == 0 && mode == 0x105, "standard suggestion");

        desc.Dimension = 2;

        Check(suggest(&desc, 0, &mode) == 0 && mode == 0x100, "1D suggestion");

        Check(suggest(null, 0, &mode) != 0, "null description");

        desc.Dimension = 3;

        desc.Flags = 0x300005;

        Check(
            TextureFootprintCalculator.TryCalculate(desc, out var size, out var alignment) && size == 33423360
            && alignment == 65536, "4K resource regression");

        desc.Width = 256;

        desc.Height = 256;

        desc.Format = 98;

        desc.Flags = 0;

        desc.Layout = 0x10A;

        desc.MipLevels = 0;

        nint self = 0;

        Check(create(&desc, &self) == 0 && self != 0, "BC7 computer creation");

        try
        {
            var get = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(self, 2);
            var output = stackalloc byte[0x1B40];

            Check(get(self, output) == 0, "layout output");

            Check(
                *(ulong*)output == 131072 && *(ulong*)(output + 8) == 65536 && *(uint*)(output + 16) == 9
                && *(uint*)(output + 20) == 1, "BC7 chain size");

            var plane = output + 24;
            var mip = plane + 56;

            Check(*(uint*)(plane + 44) == 1 && *(uint*)(plane + 48) == 8, "BC7 mip tail count");

            Check(*(ulong*)(mip + 8) == 65536 && *(uint*)(mip + 28) == 1024, "BC7 mip 0 offset and pitch");

            Check(*(ulong*)(mip + 0x70 + 8) == 32768 && *(uint*)(mip + 0x70 + 100) == 1, "BC7 mip tail offset");

            var into = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint, byte*, uint, uint, int>)Slot(self, 8);
            var from =
                (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint, uint, byte*, uint, uint, int>)Slot(self, 9);
            var tiled = new byte[131072];

            fixed (byte* tile = tiled)
            {
                for (var i = 0U; i < 9; i++)
                {
                    var m = mip + i * 0x70;
                    var row = *(uint*)(m + 48) * 16;
                    var height = *(uint*)(m + 52);
                    var source = new byte[row * height];
                    var destination = new byte[source.Length];

                    for (var b = 0; b < source.Length; b++)
                    {
                        source[b] = (byte)((uint)b ^ i);
                    }

                    fixed (byte* a = source, b = destination)
                    {
                        Check(into(self, tile, 0, i, a, row, 0) == 0, "tile mip");

                        Check(from(self, b, 0, i, 0, tile, row, 0) == 0, "detile mip");

                        Check(source.AsSpan().SequenceEqual(destination), "BC7 round trip");
                    }
                }
            }

            uint tiles = 0, count = 4;

            var packed = stackalloc byte[12];
            var shape = stackalloc uint[3];
            var subs = stackalloc byte[48];
            var tiling =
                (delegate* unmanaged[Stdcall]<nint, uint*, byte*, uint*, uint*, uint, byte*, int>)Slot(self, 10);

            Check(
                tiling(self, &tiles, packed, shape, &count, 0, subs) == 0 && tiles == 2 && packed[0] == 1
                && packed[1] == 8 && *(uint*)(packed + 4) == 1 && shape[0] == 256 && shape[1] == 256 && count == 1
                && *(uint*)(subs + 8) == 1, "64 KiB tile layout");
        }
        finally
        {
            Check(((delegate* unmanaged[Stdcall]<nint, uint>)Slot(self, 1))(self) == 0, "release");
        }

        desc.Width = 128;

        desc.Height = 4;

        desc.Format = 11;

        desc.Layout = 0x100;

        desc.MipLevels = 1;

        self = 0;

        Check(create(&desc, &self) == 0 && self != 0, "linear computer creation");

        try
        {
            var offset =
                (delegate* unmanaged[Stdcall]<nint, uint, uint, ulong, uint, uint, uint, uint*, ulong>)Slot(self, 6);

            uint bit = 7;

            Check(offset(self, 0, 0, 3, 1, 0, 0, &bit) == 1048 && bit == 0, "linear offset");

            Check(offset(self, 0, 0, 128, 0, 0, 0, null) == 0, "outside texture");
        }
        finally
        {
            Check(((delegate* unmanaged[Stdcall]<nint, uint>)Slot(self, 1))(self) == 0, "linear release");
        }

        desc.Format = 0;

        Check(create(&desc, &self) != 0 && self == 0, "invalid format");
    }
}
