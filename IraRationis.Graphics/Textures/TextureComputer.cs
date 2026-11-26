// SPDX-License-Identifier: GPL-3.0-or-later
// XG ABI adapted from Zettrox/ztrx victory-hle/src/modules/xg.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Textures;

public static unsafe class TextureComputer
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private const int Failed = unchecked((int)0x80004005);
    private const int LayoutSize = 0x1B40;
    private const int PlaneSize = 0x6C8;
    private const int MipSize = 0x70;
    private static readonly nint Vtable = BuildTable();

    public static nint SuggestAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<ConsoleGraphicsDevice.ResourceDescription*, uint, uint*, int>)
                &Suggest;
        }
    }

    public static nint CreateAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<ConsoleGraphicsDevice.ResourceDescription*, nint*, int>)&Create;
        }
    }

    private static nint BuildTable()
    {
        var table = (nint*)NativeMemory.AllocZeroed(13, (nuint)sizeof(nint));

        for (var i = 0; i < 13; i++)
        {
            table[i] = (nint)(delegate* unmanaged[Stdcall]<Computer*, int>)&Unused;
        }

        table[0] = (nint)(delegate* unmanaged[Stdcall]<Computer*, uint>)&AddRef;

        table[1] = (nint)(delegate* unmanaged[Stdcall]<Computer*, uint>)&Release;

        table[2] = (nint)(delegate* unmanaged[Stdcall]<Computer*, byte*, int>)&GetLayout;

        table[6] =
            (nint)(delegate* unmanaged[Stdcall]<Computer*, uint, uint, ulong, uint, uint, uint, uint*, ulong>)
            &ElementOffset;

        table[8] = (nint)(delegate* unmanaged[Stdcall]<Computer*, byte*, uint, uint, byte*, uint, uint, int>)&CopyInto;

        table[9] =
            (nint)(delegate* unmanaged[Stdcall]<Computer*, byte*, uint, uint, uint, byte*, uint, uint, int>)&CopyFrom;

        table[10] =
            (nint)(delegate* unmanaged[Stdcall]<Computer*, uint*, byte*, uint*, uint*, uint, byte*, int>)&Tiling;

        return (nint)table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Suggest(ConsoleGraphicsDevice.ResourceDescription* desc, uint flags, uint* output)
    {
        if (desc == null || output == null)
        {
            return InvalidArgument;
        }

        *output = 0x100 | TextureFootprintCalculator.SuggestMode(*desc);

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Create(ConsoleGraphicsDevice.ResourceDescription* desc, nint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        Computer* computer = null;

        var handle = default(GCHandle);

        try
        {
            if (desc == null || !TextureFootprintCalculator.TryCreate(*desc, out var texture)
                             || texture.Planes[0].Mips.Length > 15)
            {
                return InvalidArgument;
            }

            handle = GCHandle.Alloc(texture);

            computer = (Computer*)NativeMemory.AllocZeroed((nuint)sizeof(Computer));

            computer->Table = Vtable;

            computer->References = 1;

            computer->Handle = GCHandle.ToIntPtr(handle);

            *output = (nint)computer;

            return 0;
        }
        catch (OutOfMemoryException)
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }

            NativeMemory.Free(computer);

            return OutOfMemory;
        }
    }

    private static TextureLayout State(Computer* self)
    {
        return (TextureLayout)GCHandle.FromIntPtr(self->Handle).Target!;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Computer* self)
    {
        return (uint)Interlocked.Increment(ref self->References);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Computer* self)
    {
        var remaining = Interlocked.Decrement(ref self->References);

        if (remaining == 0)
        {
            GCHandle.FromIntPtr(self->Handle).Free();

            NativeMemory.Free(self);
        }

        return (uint)remaining;
    }

    private static void U32(byte* output, int offset, uint value)
    {
        Unsafe.WriteUnaligned(output + offset, value);
    }

    private static void U64(byte* output, int offset, ulong value)
    {
        Unsafe.WriteUnaligned(output + offset, value);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetLayout(Computer* self, byte* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        var texture = State(self);
        var desc = texture.Description;
        var mips = (uint)texture.Planes[0].Mips.Length;

        new Span<byte>(output, LayoutSize).Clear();

        U64(output, 0, texture.Size);

        U64(output, 8, texture.Alignment);

        U32(output, 16, mips);

        U32(output, 20, (uint)texture.Planes.Length);

        U32(output, 0x1B38, desc.Dimension);

        for (var i = 0; i < texture.Planes.Length; i++)
        {
            var p = texture.Planes[i];
            var plane = output + 24 + i * PlaneSize;
            var depth = (desc.Flags & 2) != 0 && (texture.Planes.Length > 1 || mips == 1);

            U32(plane, 0, i != 0 ? 8U : depth ? 7U : 1U);

            U64(plane, 8, p.Size);

            U64(plane, 16, p.SliceSize);

            U64(plane, 24, p.Offset);

            U64(plane, 32, p.Alignment);

            U32(plane, 40, p.Bytes);

            U32(plane, 44, p.FirstTail);

            U32(plane, 48, mips - p.FirstTail);

            for (var j = 0; j < p.Mips.Length; j++)
            {
                var m = p.Mips[j];
                var mip = plane + 56 + j * MipSize;
                var three = desc.Dimension == 4;
                var slice = (ulong)m.Pitch * m.Rows * p.Bytes;
                var sliceSize = m.InTail ? p.Thick ? p.Alignment / p.TileDepth : slice : slice * p.Samples;

                U64(mip, 0, sliceSize * (three ? m.DepthPitch : 1U));

                U64(mip, 8, m.InTail ? p.Thick ? 0UL : m.TailOffset : m.Offset);

                U64(mip, 16, sliceSize);

                U32(mip, 24, m.Pitch * p.Block);

                U32(mip, 28, m.Pitch * p.Bytes);

                U32(mip, 32, m.InTail ? 256U : (uint)p.Alignment);

                U32(mip, 36, m.Pitch);

                U32(mip, 40, m.Rows);

                U32(mip, 44, three ? m.DepthPitch : desc.DepthOrArraySize);

                U32(mip, 48, m.Width);

                U32(mip, 52, m.Height);

                U32(mip, 56, (uint)Math.Max(1UL, desc.Width >> j));

                U32(mip, 60, Math.Max(1U, desc.Height >> j));

                U32(mip, 64, three ? m.Depth : desc.DepthOrArraySize);

                U32(mip, 68, p.Samples);

                U32(mip, 72, m.InTail ? TailMode(p.Mode) : p.Mode);

                U32(mip, 96, p.TileDepth);

                U32(mip, 100, m.InTail ? 1U : 0U);

                U32(mip, 104, m.InTail && p.Thick ? m.TailX | (m.TailY << 11) : 0);
            }
        }

        return 0;
    }

    private static uint TailMode(uint mode)
    {
        return mode switch
        {
            2 or 6 or 10 or 18 or 22 or 26 => 2U,
            23 or 27 => 3U,
            24 => 24U,
            _ => 1U
        };
    }

    private static ulong Address(TexturePlane p, TextureMip m, uint x, uint y, uint z, uint[] xs, uint[] ys, uint[] zs)
    {
        var offset = z / p.TileDepth * p.SliceSize + m.Offset;

        if (p.Mode == 0)
        {
            return offset + ((ulong)y * m.Pitch + x) * p.Bytes;
        }

        x += m.TailX;

        y += m.TailY;

        return offset + ((ulong)(y / p.TileHeight) * (m.Pitch / p.TileWidth) + x / p.TileWidth) * p.Alignment
                      + (xs[x % p.TileWidth] | ys[y % p.TileHeight] | zs[z % p.TileDepth]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong ElementOffset(Computer* self, uint plane, uint mip, ulong x, uint y, uint z, uint sample,
        uint* bit)
    {
        if (bit != null)
        {
            *bit = 0;
        }

        var t = State(self);

        if (plane >= t.Planes.Length || sample != 0)
        {
            return 0;
        }

        var p = t.Planes[plane];

        if (mip >= p.Mips.Length || !p.Addressable)
        {
            return 0;
        }

        var m = p.Mips[mip];

        if (x >= m.Width || y >= m.Height || z >= (t.Description.Dimension == 4 ? m.Depth : p.Slices))
        {
            return 0;
        }

        try
        {
            var patterns = p.Patterns();
            var at = Address(p, m, (uint)x, y, z, patterns[0], patterns[1], patterns[2]);

            return at <= p.Size && p.Bytes <= p.Size - at ? p.Offset + at : 0;
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CopyInto(Computer* self, byte* tiled, uint plane, uint subresource, byte* linear, uint row,
        uint depth)
    {
        return Copy(self, tiled, plane, subresource, linear, row, depth, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CopyFrom(Computer* self, byte* linear, uint plane, uint subresource, uint sample, byte* tiled,
        uint row, uint depth)
    {
        return sample == 0 ? Copy(self, tiled, plane, subresource, linear, row, depth, false) : NotImplemented;
    }

    private static int Copy(Computer* self, byte* tiled, uint plane, uint subresource, byte* linear, uint row,
        uint depth, bool into)
    {
        var t = State(self);

        if (tiled == null || linear == null || plane >= t.Planes.Length)
        {
            return InvalidArgument;
        }

        var p = t.Planes[plane];

        if (!p.Addressable)
        {
            return NotImplemented;
        }

        var m = p.Mips[subresource % (uint)p.Mips.Length];
        var slice = subresource / (uint)p.Mips.Length;
        var three = t.Description.Dimension == 4;
        var bytes = (ulong)m.Width * p.Bytes;

        if ((three && slice != 0) || slice >= p.Slices || row < bytes
            || (m.Depth > 1 && depth < (ulong)(m.Height - 1) * row + bytes))
        {
            return InvalidArgument;
        }

        try
        {
            var patterns = p.Patterns();
            var xs = patterns[0];
            var ys = patterns[1];
            var zs = patterns[2];
            var runs = new List<(uint X, uint Length)>();
            var column = 0U;

            while (column < m.Width)
            {
                var at = Address(p, m, column, 0, slice, xs, ys, zs);
                var run = p.Mode == 0 ? m.Width - column : 1U;

                while (column + run < m.Width
                       && Address(p, m, column + run, 0, slice, xs, ys, zs) == at + (ulong)run * p.Bytes)
                {
                    run++;
                }

                runs.Add((column, run));

                column += run;
            }

            // The contiguous column runs are independent of row and depth.
            for (var z = 0U; z < m.Depth; z++)
            {
                for (var y = 0U; y < m.Height; y++)
                {
                    foreach (var (x, run) in runs)
                    {
                        var at = Address(p, m, x, y, slice + z, xs, ys, zs);
                        var length = (ulong)run * p.Bytes;

                        if (at > p.Size || length > p.Size - at)
                        {
                            return InvalidArgument;
                        }

                        var a = tiled + checked((nint)(p.Offset + at));
                        var b = linear + checked((nint)((ulong)z * depth + (ulong)y * row + (ulong)x * p.Bytes));

                        Buffer.MemoryCopy(into ? b : a, into ? a : b, length, length);
                    }
                }
            }

            return 0;
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
        catch (OverflowException)
        {
            return InvalidArgument;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Tiling(Computer* self, uint* tiles, byte* packed, uint* shape, uint* count, uint first,
        byte* output)
    {
        var t = State(self);
        var p = t.Planes[0];

        const ulong tile = 65536;

        if (p.Alignment != tile)
        {
            return Failed;
        }

        if (t.Description.Dimension != 3 || p.Thick || p.Size / tile > uint.MaxValue)
        {
            return NotImplemented;
        }

        var standard = p.FirstTail;
        var tail = standard < p.Mips.Length;
        var tailTiles = standard == 0 ? p.SliceSize / tile : tail ? p.Mips[standard - 1].Offset / tile : 0;

        if (tiles != null)
        {
            *tiles = (uint)(p.Size / tile);
        }

        if (packed != null)
        {
            new Span<byte>(packed, 12).Clear();

            packed[0] = (byte)standard;

            packed[1] = (byte)(p.Mips.Length - standard);

            U32(packed, 4, (uint)tailTiles);
        }

        if (shape != null)
        {
            shape[0] = p.TileWidth * p.Block;

            shape[1] = p.TileHeight * p.Block;

            shape[2] = p.TileDepth;
        }

        if (count != null)
        {
            var available = standard * (tail ? Math.Min(p.Slices, 2U) : p.Slices);
            var wanted = Math.Min(*count, available > first ? available - first : 0);

            if (wanted != 0 && output == null)
            {
                return InvalidArgument;
            }

            for (var i = 0U; i < wanted; i++)
            {
                var slice = (first + i) / standard;
                var m = p.Mips[(first + i) % standard];
                var entry = output + 12 * i;

                new Span<byte>(entry, 12).Clear();

                if (!tail || slice == 0)
                {
                    U32(entry, 0, m.Pitch / p.TileWidth);

                    Unsafe.WriteUnaligned(entry + 4, (ushort)(m.Rows / p.TileHeight));

                    Unsafe.WriteUnaligned(entry + 6, (ushort)1);

                    U32(entry, 8, (uint)((slice * p.SliceSize + m.Offset) / tile));
                }
            }

            *count = wanted;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unused(Computer* self)
    {
        return NotImplemented;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Computer
    {
        public nint Table;
        public int References;
        public nint Handle;
    }
}
