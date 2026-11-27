// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/tls.rs.

using System.Buffers.Binary;
using IraRationis.Loader.Memory;
using IraRationis.PE.Parsing;

namespace IraRationis.Loader.Threads;

public static unsafe class TlsRedirector
{
    private const int LoadLength = 9;
    private const int ThunkSize = 32;
    private const uint ExecutableSection = 0x20000000;

    internal static int Redirect(nuint address, PeImage info)
    {
        if (info.Tls is not { } tls)
        {
            return 0;
        }

        if ((ulong)tls.DirectoryRva + 24 > info.SizeOfImage)
        {
            throw new InvalidDataException("TLS directory exceeds image.");
        }

        var indexAddress = *(nuint*)(address + tls.DirectoryRva + 16);

        if (indexAddress < address || indexAddress - address + 4 > info.SizeOfImage)
        {
            throw new InvalidDataException("TLS index pointer exceeds image.");
        }

        var index = *(uint*)indexAddress;

        if (index == 0)
        {
            return 0;
        }

        var page = AllocateNear(address, info.SizeOfImage);
        var code = new Span<byte>((void*)page, 4096);

        code.Fill(0xCC);

        for (var register = 0; register < 16; register++)
        {
            EmitRegisterThunk(code.Slice(register * ThunkSize, ThunkSize), register, index);
        }

        ExecutableMemory.Seal(page, 4096);

        var patched = 0;

        foreach (var section in info.Sections)
        {
            if ((section.Characteristics & ExecutableSection) == 0 || section.VirtualSize == 0)
            {
                continue;
            }

            if ((ulong)section.Rva + section.VirtualSize > info.SizeOfImage)
            {
                throw new InvalidDataException("Executable section exceeds image.");
            }

            var start = address + section.Rva;
            var bytes = new ReadOnlySpan<byte>((void*)start, checked((int)section.VirtualSize)).ToArray();
            var count = PatchLoads(bytes, start, (nuint)page);

            if (count <= 0)
            {
                continue;
            }

            ExecutableMemory.Write((nint)start, bytes, true);

            patched += count;
        }

        ExecutableMemory.Write((nint)indexAddress, new byte[4], false);

        return patched;
    }

    public static int PatchLoads(Span<byte> code, nuint address, nuint thunks)
    {
        var count = 0;
        var offset = 0;

        while (offset <= code.Length - LoadLength)
        {
            var instruction = code.Slice(offset, LoadLength);
            var rex = instruction[1];
            var register = ((instruction[3] >> 3) & 7) | ((rex & 4) << 1);

            if (instruction[0] == 0x65 && rex is 0x48 or 0x4C && instruction[2] == 0x8B
                && (instruction[3] & 0xC7) == 4 && instruction[4] == 0x25 && instruction[5] == 0x58
                && instruction[6] == 0 && instruction[7] == 0 && instruction[8] == 0 && register != 4)
            {
                var relative = checked((long)thunks + register * ThunkSize - ((long)address + offset + 5));

                instruction[0] = 0xE8;

                BinaryPrimitives.WriteInt32LittleEndian(instruction[1..], checked((int)relative));

                instruction[5] = 0x0F;

                instruction[6] = 0x1F;

                instruction[7] = 0x40;

                instruction[8] = 0;

                count++;

                offset += LoadLength;
            }
            else
            {
                offset++;
            }
        }

        return count;
    }

    private static void EmitRegisterThunk(Span<byte> code, int register, uint index)
    {
        var high = register >> 3;
        var low = register & 7;
        var load = new byte[] { 0x65, (byte)(0x48 | (high << 2)), 0x8B, (byte)((low << 3) | 4), 0x25, 0x58, 0, 0, 0 };

        load.CopyTo(code);

        code[9] = (byte)(0x48 | (high << 2) | high);

        code[10] = 0x8D;

        code[11] = (byte)(0x80 | (low << 3) | low);

        var cursor = 12;

        if (low == 4)
        {
            code[cursor++] = 0x24;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(code[cursor..], checked(index * 8));

        code[cursor + 4] = 0xC3;
    }

    private static nint AllocateNear(nuint address, uint size)
    {
        var granule = (nuint)0x10000;

        if (size >= 1U << 30)
        {
            throw new InvalidDataException("Image too large for relative TLS calls.");
        }

        var above = (address + size + granule - 1) & ~(granule - 1);

        for (var index = 0; index < 16384; index++)
        {
            var delta = (nuint)(index + 1) * granule;

            if (address > delta)
            {
                var below = WindowsNative.VirtualAlloc((nint)(address - delta), 4096, WindowsNative.CommitReserve,
                    WindowsNative.ReadWrite);

                if (below != 0)
                {
                    return below;
                }
            }

            var page = WindowsNative.VirtualAlloc((nint)(above + (nuint)index * granule), 4096,
                WindowsNative.CommitReserve,
                WindowsNative.ReadWrite);

            if (page != 0)
            {
                return page;
            }
        }

        throw new InvalidOperationException("No free page near image for TLS thunks.");
    }
}
