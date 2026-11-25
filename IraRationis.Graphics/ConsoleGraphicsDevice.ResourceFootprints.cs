// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI and placement rules adapted from Zettrox/ztrx victory-gpu/src/resource.rs.

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static void RegisterFootprintMethods(nint* device)
    {
        if (sizeof(ResourceDescription1) != 72 || sizeof(AllocationInfo1) != 24 || sizeof(CopyFootprint) != 32)
        {
            throw new InvalidOperationException("Unexpected footprint ABI layout.");
        }

        device[38] =
            (nint)(delegate* unmanaged[Stdcall]<nint, ResourceDescription*, uint, uint, ulong, CopyFootprint*, uint*,
                ulong*, ulong*, void>)&GetCopyableFootprints;
        device[108] =
            (nint)(delegate* unmanaged[Stdcall]<AllocationInfo*, nint, uint, uint, ResourceDescription1*,
                AllocationInfo1*, AllocationInfo*>)&GetResourceAllocationInfo2;
    }

    private static ulong AlignFootprint(ulong value, ulong alignment)
    {
        return checked(value + alignment - 1) & ~(alignment - 1);
    }

    private static bool TryCopyFormat(uint format, out uint element, out uint block)
    {
        block = 1;
        element = format switch
        {
            >= 1 and <= 4 => 16,
            >= 5 and <= 8 => 12,
            >= 9 and <= 22 => 8,
            >= 23 and <= 47 or 67 or >= 87 and <= 93 or 116 or 117 or 189 or 191 => 4,
            >= 118 and <= 120 => 3,
            >= 48 and <= 59 or 68 or 69 or 85 or 86 or 115 => 2,
            >= 60 and <= 65 or 190 => 1,
            >= 70 and <= 72 or >= 79 and <= 81 => 8,
            >= 73 and <= 78 or >= 82 and <= 84 or >= 94 and <= 99 => 16,
            _ => 0
        };
        if (format is >= 70 and <= 84 or >= 94 and <= 99)
        {
            block = 4;
        }

        return element != 0;
    }

    private static bool TryCopyFootprint(ResourceDescription desc, uint index, out CopyFootprint footprint,
        out uint rows, out ulong rowSize)
    {
        footprint = default;
        rows = 0;
        rowSize = 0;
        if (desc.Width == 0 || desc.SampleCount > 1)
        {
            return false;
        }

        if (desc.Dimension == 1)
        {
            if (index != 0 || desc.Width > uint.MaxValue || AlignFootprint(desc.Width, 256) > uint.MaxValue)
            {
                return false;
            }

            footprint = new CopyFootprint
                { Width = (uint)desc.Width, Height = 1, Depth = 1, RowPitch = (uint)AlignFootprint(desc.Width, 256) };
            rows = 1;
            rowSize = desc.Width;

            return true;
        }

        if (desc.Dimension is < 2 or > 4 || desc.Height == 0 || desc.DepthOrArraySize == 0 || desc.Width > uint.MaxValue
            || !TryCopyFormat(desc.Format, out var element, out var block))
        {
            return false;
        }

        var extent = Math.Max(desc.Width, Math.Max(desc.Height, desc.Dimension == 4 ? desc.DepthOrArraySize : 1UL));
        var mips = desc.MipLevels == 0 ? (uint)(BitOperations.Log2(extent) + 1) : desc.MipLevels;
        var slices = desc.Dimension == 4 ? 1U : desc.DepthOrArraySize;
        if (mips > 32 || index / mips >= slices)
        {
            return false;
        }

        var mip = (int)(index % mips);
        var width = AlignFootprint(Math.Max(1UL, desc.Width >> mip), block);
        var height = AlignFootprint(Math.Max(1UL, (ulong)desc.Height >> mip), block);
        var depth = desc.Dimension == 4 ? Math.Max(1U, (uint)desc.DepthOrArraySize >> mip) : 1U;
        rowSize = checked(width / block * element);
        var pitch = AlignFootprint(rowSize, 256);
        if (width > uint.MaxValue || height > uint.MaxValue || pitch > uint.MaxValue)
        {
            return false;
        }

        rows = (uint)(height / block);
        footprint = new CopyFootprint
            { Format = desc.Format, Width = (uint)width, Height = (uint)height, Depth = depth, RowPitch = (uint)pitch };

        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void GetCopyableFootprints(nint self, ResourceDescription* description, uint first, uint count,
        ulong baseOffset, CopyFootprint* layouts, uint* rows, ulong* rowSizes, ulong* total)
    {
        if (total != null)
        {
            *total = ulong.MaxValue;
        }

        if (count > 65536)
        {
            return;
        }

        var end = baseOffset;
        var valid = description != null;
        for (var index = 0U; index < count; index++)
        {
            var footprint = new CopyFootprint { Offset = ulong.MaxValue };
            var rowCount = uint.MaxValue;
            var rowSize = ulong.MaxValue;
            try
            {
                if (valid && TryCopyFootprint(*description, checked(first + index), out var candidate,
                        out var candidateRows, out var candidateRowSize))
                {
                    candidate.Offset = AlignFootprint(end, 512);
                    end = checked(candidate.Offset + candidate.RowPitch * ((ulong)candidateRows * candidate.Depth - 1)
                                                   + candidateRowSize);
                    footprint = candidate;
                    rowCount = candidateRows;
                    rowSize = candidateRowSize;
                }
                else
                {
                    valid = false;
                }
            }
            catch (OverflowException)
            {
                valid = false;
            }

            if (layouts != null)
            {
                layouts[index] = footprint;
            }

            if (rows != null)
            {
                rows[index] = rowCount;
            }

            if (rowSizes != null)
            {
                rowSizes[index] = rowSize;
            }
        }

        if (valid && total != null)
        {
            *total = end - baseOffset;
        }

        if (!valid)
        {
            Console.Error.WriteLine("[graphics] GetCopyableFootprints: invalid or unsupported subresource.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static AllocationInfo* GetResourceAllocationInfo2(AllocationInfo* output, nint self, uint visible,
        uint count, ResourceDescription1* descriptions, AllocationInfo1* each)
    {
        if (output == null)
        {
            return null;
        }

        *output = new AllocationInfo { Size = ulong.MaxValue };
        if (count > 65536 || (count != 0 && descriptions == null))
        {
            return output;
        }

        if (each != null)
        {
            for (var index = 0U; index < count; index++)
            {
                each[index] = new AllocationInfo1 { Offset = ulong.MaxValue, Size = ulong.MaxValue };
            }
        }

        var size = 0UL;
        var alignment = ResourcePage;
        try
        {
            for (var index = 0U; index < count; index++)
            {
                var description = descriptions[index].Description;
                if (!ResourceFootprint(description, out var info))
                {
                    ReportResourceAllocation(index, count, description, "unsupported allocation info2 footprint");

                    return output;
                }

                var offset = AlignFootprint(size, info.Alignment);
                size = checked(offset + info.Size);
                alignment = Math.Max(alignment, info.Alignment);
                if (each != null)
                {
                    each[index] = new AllocationInfo1 { Offset = offset, Alignment = info.Alignment, Size = info.Size };
                }
            }
        }
        catch (OverflowException)
        {
            return output;
        }

        *output = new AllocationInfo { Size = size, Alignment = alignment };

        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResourceDescription1
    {
        public ResourceDescription Description;
        public uint RegionWidth;
        public uint RegionHeight;
        public uint RegionDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AllocationInfo1
    {
        public ulong Offset;
        public ulong Alignment;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyFootprint
    {
        public ulong Offset;
        public uint Format;
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint RowPitch;
    }
}
