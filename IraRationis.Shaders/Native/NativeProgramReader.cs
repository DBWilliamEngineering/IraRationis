// SPDX-License-Identifier: GPL-3.0-or-later
// XDAT/XBCH/FSSM format adapted from DexNom/VIctory victory-shader/src/native.rs.

using System.Buffers.Binary;
using System.IO.Compression;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native.Models;

namespace IraRationis.Shaders.Native;

public static class NativeProgramReader
{
    private const int MaximumBlobBytes = 16 * 1024 * 1024;
    private const int MaximumNativeBytes = 64 * 1024 * 1024;
    private const ulong Absent = 0xDEADBEEFDEADBEEF;
    private static readonly int[] SlotOffsets = [8, 16, 24, 32, 40, 48, 0x48, 0x60];

    public static IReadOnlyList<NativeProgram> Read(ShaderContainer container)
    {
        var part = container.Part("DXIL");

        return part == null ? [] : ReadPart(part.Data.Span);
    }

    public static IReadOnlyList<NativeProgram> ReadPart(ReadOnlySpan<byte> part)
    {
        var programs = new List<NativeProgram>();

        if (part.Length < 28 || !part.Slice(24, 4).SequenceEqual("XDAT"u8))
        {
            return programs;
        }

        var count = U32(part, 28);

        if (count > 64)
        {
            throw new InvalidDataException("Too many XDAT entries.");
        }

        _ = Slice(part, 36, 12UL * count);

        ulong unpackedTotal = 0;

        for (var index = 0; index < count; index++)
        {
            var byteOffset = 36 + 12 * index;

            if (U32(part, byteOffset) != 0x0d)
            {
                continue;
            }

            var blob = Slice(part, U32(part, byteOffset + 4), U32(part, byteOffset + 8));

            if (blob.Length < 24 || !blob[..4].SequenceEqual("XBCH"u8) || U32(blob, 4) != 3)
            {
                throw new InvalidDataException("Unsupported XBCH header.");
            }

            var unpacked = U64(blob, 16);

            if (unpacked > MaximumBlobBytes || unpacked > MaximumNativeBytes - unpackedTotal)
            {
                throw new InvalidDataException("Native shader decompression budget exceeded.");
            }

            unpackedTotal += unpacked;

            var packed = Slice(blob, 24, U64(blob, 8));

            using var input = new MemoryStream(packed.ToArray(), false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);

            var raw = new byte[(int)unpacked];

            zlib.ReadExactly(raw);

            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException("XBCH expands beyond its declared size.");
            }

            ReadFssm(raw, programs);
        }

        return programs;
    }

    private static void ReadFssm(ReadOnlySpan<byte> raw, List<NativeProgram> programs)
    {
        if (raw.Length < 16 || !raw[..4].SequenceEqual("FSSM"u8) || U32(raw, 4) != 0x101 ||
            U64(raw, 8) != (ulong)raw.Length - 16)
        {
            throw new InvalidDataException("Unsupported FSSM header.");
        }

        var payload = raw[16..];

        if (U64(payload, 0) != 6)
        {
            throw new InvalidDataException("Unexpected FSSM stage count.");
        }

        for (var index = 0; index < SlotOffsets.Length; index++)
        {
            var offset = U64(payload, SlotOffsets[index]);

            if (offset == Absent)
            {
                continue;
            }

            var record = Slice(payload, offset, 0x54);
            var code = Slice(payload, U64(record, 0), U64(record, 8));

            if (code.IsEmpty || (code.Length & 3) != 0 || code.Length > MaximumBlobBytes)
            {
                throw new InvalidDataException("Invalid GFX10 code length.");
            }

            var registers = U32(record, 0x50);

            if ((registers & 0x3ff) > 256)
            {
                throw new InvalidDataException("Invalid VGPR count.");
            }

            var root = ReadTable(payload, U64(record, 0x28), U32(record, 0x38));
            var used = ReadTable(payload, U64(record, 0x30), U32(record, 0x3c));
            var stage = (NativeShaderStage)index;
            var stageMetadata = ReadStageMetadata(payload, offset, stage);

            uint[]? threads = null;

            if (stage == NativeShaderStage.Compute)
            {
                var io = Slice(payload, offset + 0x54, 0x34);

                threads = [U32(io, 0x28), U32(io, 0x2c), U32(io, 0x30)];
            }

            if (programs.Sum(program =>
                    (long)program.Code.Length + program.RootLayout.Length + program.UsedResources.Length + program.StageMetadata.Length) +
                code.Length + root.Length + used.Length + stageMetadata.Length > MaximumNativeBytes)
            {
                throw new InvalidDataException("Native program retention budget exceeded.");
            }

            programs.Add(new NativeProgram(stage, (U32(record, 0x14) & 32) == 0,
                registers & 0x3ff, (registers >> 20) & 0xff, U32(record, 0x4c), root, used, threads, code.ToArray())
            {
                StageMetadata = stageMetadata
            });
        }
    }

    private static byte[] ReadStageMetadata(ReadOnlySpan<byte> payload, ulong recordOffset, NativeShaderStage stage)
    {
        var length = stage switch
        {
            NativeShaderStage.Vertex => 0x18C,
            NativeShaderStage.Pixel => 0x14C + 8 * 404,
            _ => 0
        };
        var metadataOffset = recordOffset + 0x54;

        if (length == 0 || metadataOffset > (ulong)payload.Length ||
            (ulong)length > (ulong)payload.Length - metadataOffset)
        {
            return [];
        }

        return payload.Slice((int)metadataOffset, length).ToArray();
    }

    private static byte[] ReadTable(ReadOnlySpan<byte> payload, ulong offset, uint count)
    {
        return count switch
        {
            0 => [],
            > 65536 =>
                throw new InvalidDataException("Excessive shader resource table count."),
            _ => Slice(payload, offset, 20UL * count).ToArray()
        };
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, ulong offset, ulong length)
    {
        if (offset > (ulong)data.Length || length > (ulong)data.Length - offset)
        {
            throw new InvalidDataException("Shader native record outside container.");
        }

        return data.Slice((int)offset, (int)length);
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, (ulong)offset, 4));
    }

    private static ulong U64(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(Slice(data, (ulong)offset, 8));
    }
}
