// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-loader/src/prepare.rs.

using System.Buffers.Binary;
using Blake3;
using IraRationis.Core;
using IraRationis.PE;

namespace IraRationis.Loader;

public sealed record PreparedImage(PeImage Info, byte[] Bytes, string OriginalHash)
{
    public string FileName
    {
        get
        {
            return $"irx-image-{OriginalHash[..16]}.dll";
        }
    }
}

public static class ImagePreparation
{
    private const int ImportDescriptorSize = 20;
    private const int TlsCallbackOffset = 24;
    private const int DirectoryOffset = 112;
    private const int DirectorySize = 8;

    public static PreparedImage Prepare(byte[] original)
    {
        var info = PeImage.Parse(original);
        var bytes = (byte[])original.Clone();
        var fileHeader = checked((int)BinaryData.ReadUInt32(bytes, 0x3C) + 4);
        var optionalHeader = checked(fileHeader + 20);

        WriteUInt16(bytes, fileHeader + 18, (ushort)(info.Characteristics | 0x2000));

        WriteUInt32(bytes, optionalHeader + 16, 0);

        WriteUInt32(bytes, optionalHeader + 64, 0);

        WriteUInt16(bytes, optionalHeader + 70, (ushort)(info.DllCharacteristics & ~0x0080));

        var importOffset = GetDirectoryOffset(bytes, fileHeader, optionalHeader, 1);

        if (importOffset is { } import)
        {
            var rva = BinaryData.ReadUInt32(bytes, import);

            if (rva != 0)
            {
                var terminator = checked(rva + (uint)info.Imports.Count * ImportDescriptorSize);
                var terminatorOffset = info.RvaToFileOffset(terminator)
                                       ?? throw new InvalidDataException("Import terminator is not file-backed.");

                if (BinaryData.Slice(bytes, terminatorOffset, ImportDescriptorSize).IndexOfAnyExcept((byte)0) >= 0)
                {
                    throw new InvalidDataException("Import terminator is not empty.");
                }

                WriteUInt32(bytes, import, terminator);

                WriteUInt32(bytes, import + 4, ImportDescriptorSize);
            }
        }

        var boundOffset = GetDirectoryOffset(bytes, fileHeader, optionalHeader, 11);

        if (boundOffset is { } bound)
        {
            bytes.AsSpan(bound, DirectorySize).Clear();
        }

        if (info.Tls is { CallbacksVa: not 0 } tls)
        {
            var offset = info.RvaToFileOffset(tls.DirectoryRva)
                         ?? throw new InvalidDataException("TLS directory is not file-backed.");
            var fieldRva = checked(tls.DirectoryRva + TlsCallbackOffset);

            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(checked((int)offset + TlsCallbackOffset), 8), 0);

            ClearTlsRelocation(bytes, info, fieldRva);
        }

        var hash = Hasher.Hash(original);

        return new PreparedImage(info, bytes, hash.ToString());
    }

    private static int? GetDirectoryOffset(byte[] bytes, int fileHeader, int optionalHeader, int index)
    {
        var optionalSize = BinaryData.ReadUInt16(bytes, fileHeader + 16);

        if (optionalSize < DirectoryOffset)
        {
            return null;
        }

        var count = BinaryData.ReadUInt32(bytes, optionalHeader + 108);
        var relative = checked(DirectoryOffset + index * DirectorySize);

        if ((uint)index >= count || relative > optionalSize - DirectorySize)
        {
            return null;
        }

        return checked(optionalHeader + relative);
    }

    private static void ClearTlsRelocation(byte[] bytes, PeImage info, uint fieldRva)
    {
        var directory = info.Directory(5);
        var start = info.RvaToFileOffset(directory.Rva);

        if (!directory.Present || start is null)
        {
            return;
        }

        var cursor = (long)start.Value;
        var end = cursor + directory.Size;

        BinaryData.Slice(bytes, cursor, directory.Size);

        while (cursor + 8 <= end)
        {
            var page = BinaryData.ReadUInt32(bytes, cursor);
            var block = BinaryData.ReadUInt32(bytes, cursor + 4);

            if (block < 8 || (block & 1) != 0 || block > end - cursor)
            {
                throw new InvalidDataException("Invalid relocation block size.");
            }

            for (var entry = cursor + 8; entry < cursor + block; entry += 2)
            {
                var value = BinaryData.ReadUInt16(bytes, entry);

                if (value >> 12 == 10 && (ulong)page + (uint)(value & 0xFFF) == fieldRva)
                {
                    WriteUInt16(bytes, checked((int)entry), 0);
                }
            }

            cursor += block;
        }
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
