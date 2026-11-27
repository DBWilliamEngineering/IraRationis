// SPDX-License-Identifier: GPL-3.0-or-later
// XG ABI adapted from Zettrox/ztrx victory-hle/src/modules/xg.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

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
            return (nint)(delegate* unmanaged[Stdcall]<ResourceDescription*, uint, uint*, int>)
                &Suggest;
        }
    }

    public static nint CreateAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<ResourceDescription*, nint*, int>)&Create;
        }
    }

    private static nint BuildTable()
    {
        var table = (nint*)NativeMemory.AllocZeroed(13, (nuint)sizeof(nint));

        for (var index = 0; index < 13; index++)
        {
            table[index] = (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, int>)&Unused;
        }

        table[0] = (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, uint>)&AddRef;

        table[1] = (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, uint>)&Release;

        table[2] = (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, byte*, int>)&GetLayout;

        table[6] =
            (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, uint, uint, ulong, uint, uint, uint, uint*,
                ulong>)
            &ElementOffset;

        table[8] =
            (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, byte*, uint, uint, byte*, uint, uint, int>)
            &CopyInto;

        table[9] =
            (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, byte*, uint, uint, uint, byte*, uint, uint, int
                >)&CopyFrom;

        table[10] =
            (nint)(delegate* unmanaged[Stdcall]<NativeTextureComputer*, uint*, byte*, uint*, uint*, uint, byte*, int>)
            &Tiling;

        return (nint)table;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Suggest(ResourceDescription* desc, uint flags, uint* output)
    {
        if (desc == null || output == null)
        {
            return InvalidArgument;
        }

        *output = 0x100 | TextureFootprintCalculator.SuggestMode(*desc);

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Create(ResourceDescription* desc, nint* output)
    {
        if (output == null)
        {
            return InvalidArgument;
        }

        *output = 0;

        NativeTextureComputer* computer = null;

        var handle = default(GCHandle);

        try
        {
            if (desc == null || !TextureFootprintCalculator.TryCreate(*desc, out var texture)
                             || texture.Planes[0].Mips.Length > 15)
            {
                return InvalidArgument;
            }

            handle = GCHandle.Alloc(texture);

            computer = (NativeTextureComputer*)NativeMemory.AllocZeroed((nuint)sizeof(NativeTextureComputer));

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

    private static TextureLayout State(NativeTextureComputer* self)
    {
        return (TextureLayout)GCHandle.FromIntPtr(self->Handle).Target!;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(NativeTextureComputer* self)
    {
        return (uint)Interlocked.Increment(ref self->References);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(NativeTextureComputer* self)
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
    private static int GetLayout(NativeTextureComputer* self, byte* output)
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

        for (var index = 0; index < texture.Planes.Length; index++)
        {
            var planeLayout = texture.Planes[index];
            var plane = output + 24 + index * PlaneSize;
            var depth = (desc.Flags & 2) != 0 && (texture.Planes.Length > 1 || mips == 1);

            U32(plane, 0, index != 0 ? 8U : depth ? 7U : 1U);

            U64(plane, 8, planeLayout.Size);

            U64(plane, 16, planeLayout.SliceSize);

            U64(plane, 24, planeLayout.Offset);

            U64(plane, 32, planeLayout.Alignment);

            U32(plane, 40, planeLayout.Bytes);

            U32(plane, 44, planeLayout.FirstTail);

            U32(plane, 48, mips - planeLayout.FirstTail);

            for (var mipIndex = 0; mipIndex < planeLayout.Mips.Length; mipIndex++)
            {
                var mipLayout = planeLayout.Mips[mipIndex];
                var mip = plane + 56 + mipIndex * MipSize;
                var three = desc.Dimension == 4;
                var slice = (ulong)mipLayout.Pitch * mipLayout.Rows * planeLayout.Bytes;
                var sliceSize = mipLayout.InTail
                    ? planeLayout.Thick ? planeLayout.Alignment / planeLayout.TileDepth : slice
                    : slice * planeLayout.Samples;

                U64(mip, 0, sliceSize * (three ? mipLayout.DepthPitch : 1U));

                U64(mip, 8, mipLayout.InTail ? planeLayout.Thick ? 0UL : mipLayout.TailOffset : mipLayout.Offset);

                U64(mip, 16, sliceSize);

                U32(mip, 24, mipLayout.Pitch * planeLayout.Block);

                U32(mip, 28, mipLayout.Pitch * planeLayout.Bytes);

                U32(mip, 32, mipLayout.InTail ? 256U : (uint)planeLayout.Alignment);

                U32(mip, 36, mipLayout.Pitch);

                U32(mip, 40, mipLayout.Rows);

                U32(mip, 44, three ? mipLayout.DepthPitch : desc.DepthOrArraySize);

                U32(mip, 48, mipLayout.Width);

                U32(mip, 52, mipLayout.Height);

                U32(mip, 56, (uint)Math.Max(1UL, desc.Width >> mipIndex));

                U32(mip, 60, Math.Max(1U, desc.Height >> mipIndex));

                U32(mip, 64, three ? mipLayout.Depth : desc.DepthOrArraySize);

                U32(mip, 68, planeLayout.Samples);

                U32(mip, 72, mipLayout.InTail ? TailMode(planeLayout.Mode) : planeLayout.Mode);

                U32(mip, 96, planeLayout.TileDepth);

                U32(mip, 100, mipLayout.InTail ? 1U : 0U);

                U32(mip, 104, mipLayout.InTail && planeLayout.Thick ? mipLayout.TailX | (mipLayout.TailY << 11) : 0);
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

    private static ulong Address(TexturePlane planeLayout, TextureMip mipLayout, uint coordinateX, uint coordinateY,
        uint coordinateZ, uint[] xAddressPatterns, uint[] yAddressPatterns, uint[] zAddressPatterns)
    {
        var offset = coordinateZ / planeLayout.TileDepth * planeLayout.SliceSize + mipLayout.Offset;

        if (planeLayout.Mode == 0)
        {
            return offset + ((ulong)coordinateY * mipLayout.Pitch + coordinateX) * planeLayout.Bytes;
        }

        coordinateX += mipLayout.TailX;

        coordinateY += mipLayout.TailY;

        return offset
               + ((ulong)(coordinateY / planeLayout.TileHeight) * (mipLayout.Pitch / planeLayout.TileWidth)
                  + coordinateX / planeLayout.TileWidth) * planeLayout.Alignment
               + (xAddressPatterns[coordinateX % planeLayout.TileWidth]
                  | yAddressPatterns[coordinateY % planeLayout.TileHeight]
                  | zAddressPatterns[coordinateZ % planeLayout.TileDepth]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong ElementOffset(NativeTextureComputer* self, uint plane, uint mip, ulong coordinateX,
        uint coordinateY, uint coordinateZ, uint sample,
        uint* bit)
    {
        if (bit != null)
        {
            *bit = 0;
        }

        var textureLayout = State(self);

        if (plane >= textureLayout.Planes.Length || sample != 0)
        {
            return 0;
        }

        var planeLayout = textureLayout.Planes[plane];

        if (mip >= planeLayout.Mips.Length || !planeLayout.Addressable)
        {
            return 0;
        }

        var mipLayout = planeLayout.Mips[mip];

        if (coordinateX >= mipLayout.Width || coordinateY >= mipLayout.Height || coordinateZ
            >= (textureLayout.Description.Dimension == 4 ? mipLayout.Depth : planeLayout.Slices))
        {
            return 0;
        }

        try
        {
            var patterns = planeLayout.Patterns();
            var tiledOffset = Address(planeLayout, mipLayout, (uint)coordinateX, coordinateY, coordinateZ, patterns[0],
                patterns[1], patterns[2]);

            return tiledOffset <= planeLayout.Size && planeLayout.Bytes <= planeLayout.Size - tiledOffset
                ? planeLayout.Offset + tiledOffset
                : 0;
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CopyInto(NativeTextureComputer* self, byte* tiled, uint plane, uint subresource, byte* linear,
        uint row,
        uint depth)
    {
        return Copy(self, tiled, plane, subresource, linear, row, depth, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CopyFrom(NativeTextureComputer* self, byte* linear, uint plane, uint subresource, uint sample,
        byte* tiled,
        uint row, uint depth)
    {
        return sample == 0 ? Copy(self, tiled, plane, subresource, linear, row, depth, false) : NotImplemented;
    }

    private static int Copy(NativeTextureComputer* self, byte* tiled, uint plane, uint subresource, byte* linear,
        uint row,
        uint depth, bool into)
    {
        var textureLayout = State(self);

        if (tiled == null || linear == null || plane >= textureLayout.Planes.Length)
        {
            return InvalidArgument;
        }

        var planeLayout = textureLayout.Planes[plane];

        if (!planeLayout.Addressable)
        {
            return NotImplemented;
        }

        var mipLayout = planeLayout.Mips[subresource % (uint)planeLayout.Mips.Length];
        var slice = subresource / (uint)planeLayout.Mips.Length;
        var three = textureLayout.Description.Dimension == 4;
        var bytes = (ulong)mipLayout.Width * planeLayout.Bytes;

        if ((three && slice != 0) || slice >= planeLayout.Slices || row < bytes
            || (mipLayout.Depth > 1 && depth < (ulong)(mipLayout.Height - 1) * row + bytes))
        {
            return InvalidArgument;
        }

        try
        {
            var patterns = planeLayout.Patterns();
            var xAddressPatterns = patterns[0];
            var yAddressPatterns = patterns[1];
            var zAddressPatterns = patterns[2];
            var runs = new List<(uint X, uint Length)>();
            var column = 0U;

            while (column < mipLayout.Width)
            {
                var tiledOffset = Address(planeLayout, mipLayout, column, 0, slice, xAddressPatterns, yAddressPatterns,
                    zAddressPatterns);
                var run = planeLayout.Mode == 0 ? mipLayout.Width - column : 1U;

                while (column + run < mipLayout.Width
                       && Address(planeLayout, mipLayout, column + run, 0, slice, xAddressPatterns, yAddressPatterns,
                           zAddressPatterns) == tiledOffset + (ulong)run * planeLayout.Bytes)
                {
                    run++;
                }

                runs.Add((column, run));

                column += run;
            }

            // The contiguous column runs are independent of row and depth.
            for (var coordinateZ = 0U; coordinateZ < mipLayout.Depth; coordinateZ++)
            {
                for (var coordinateY = 0U; coordinateY < mipLayout.Height; coordinateY++)
                {
                    foreach (var (x, run) in runs)
                    {
                        var tiledOffset = Address(planeLayout, mipLayout, x, coordinateY, slice + coordinateZ,
                            xAddressPatterns, yAddressPatterns, zAddressPatterns);
                        var length = (ulong)run * planeLayout.Bytes;

                        if (tiledOffset > planeLayout.Size || length > planeLayout.Size - tiledOffset)
                        {
                            return InvalidArgument;
                        }

                        var tiledElement = tiled + checked((nint)(planeLayout.Offset + tiledOffset));
                        var linearElement = linear + checked((nint)((ulong)coordinateZ * depth
                                                                    + (ulong)coordinateY * row
                                                                    + (ulong)x * planeLayout.Bytes));

                        Buffer.MemoryCopy(into ? linearElement : tiledElement, into ? tiledElement : linearElement,
                            length, length);
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
    private static int Tiling(NativeTextureComputer* self, uint* tiles, byte* packed, uint* shape, uint* count,
        uint first,
        byte* output)
    {
        var textureLayout = State(self);
        var planeLayout = textureLayout.Planes[0];

        const ulong tile = 65536;

        if (planeLayout.Alignment != tile)
        {
            return Failed;
        }

        if (textureLayout.Description.Dimension != 3 || planeLayout.Thick || planeLayout.Size / tile > uint.MaxValue)
        {
            return NotImplemented;
        }

        var standard = planeLayout.FirstTail;
        var tail = standard < planeLayout.Mips.Length;
        var tailTiles = standard == 0 ? planeLayout.SliceSize / tile :
            tail ? planeLayout.Mips[standard - 1].Offset / tile : 0;

        if (tiles != null)
        {
            *tiles = (uint)(planeLayout.Size / tile);
        }

        if (packed != null)
        {
            new Span<byte>(packed, 12).Clear();

            packed[0] = (byte)standard;

            packed[1] = (byte)(planeLayout.Mips.Length - standard);

            U32(packed, 4, (uint)tailTiles);
        }

        if (shape != null)
        {
            shape[0] = planeLayout.TileWidth * planeLayout.Block;

            shape[1] = planeLayout.TileHeight * planeLayout.Block;

            shape[2] = planeLayout.TileDepth;
        }

        if (count != null)
        {
            var available = standard * (tail ? Math.Min(planeLayout.Slices, 2U) : planeLayout.Slices);
            var wanted = Math.Min(*count, available > first ? available - first : 0);

            if (wanted != 0 && output == null)
            {
                return InvalidArgument;
            }

            for (var index = 0U; index < wanted; index++)
            {
                var slice = (first + index) / standard;
                var mipLayout = planeLayout.Mips[(first + index) % standard];
                var entry = output + 12 * index;

                new Span<byte>(entry, 12).Clear();

                if (!tail || slice == 0)
                {
                    U32(entry, 0, mipLayout.Pitch / planeLayout.TileWidth);

                    Unsafe.WriteUnaligned(entry + 4, (ushort)(mipLayout.Rows / planeLayout.TileHeight));

                    Unsafe.WriteUnaligned(entry + 6, (ushort)1);

                    U32(entry, 8, (uint)((slice * planeLayout.SliceSize + mipLayout.Offset) / tile));
                }
            }

            *count = wanted;
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unused(NativeTextureComputer* self)
    {
        return NotImplemented;
    }
}
