// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Shaders.Native;

namespace IraRationis.Shaders.Raster.PlanarColor;

internal static class PlanarNativeMetadata
{
    private const int VertexMetadataBytes = 0x18c;
    private const int PixelMetadataBytes = 0x14c + 8 * 404;
    private const int ExportPatchOffset = 0x14c;
    private const int ExportPatchBytes = 404;
    private const int ExportInstructionOffset = 0x160;
    private const int HalfExportVariant = 4;

    internal static void Validate(NativeProgram vertex, NativeProgram pixel)
    {
        ValidateVertex(vertex.StageMetadata);
        ValidatePixel(pixel.StageMetadata, pixel.Code);
    }

    private static void ValidateVertex(ReadOnlySpan<byte> metadata)
    {
        RequireExtent(metadata, VertexMetadataBytes, "vertex");
        RequireWord(metadata, 0xc4, 3, "vertex input count");
        RequireBytes(metadata, 0x24,
        [
            6, 0, 7, 4, 2,
            6, 1, 3, 8, 1,
            6, 2, 15, 12, 3
        ], "vertex semantic-to-VGPR mapping");
        RequireWord(metadata, 0x188, 2, "vertex output count");
        RequireBytes(metadata, 0xc8,
        [
            6, 1, 0, 0, 0, 0,
            6, 2, 1, 0, 0, 0
        ], "vertex parameter export mapping");
    }

    private static void ValidatePixel(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> code)
    {
        RequireExtent(metadata, PixelMetadataBytes, "pixel");
        RequireWord(metadata, 0x124, 2, "pixel input count");
        RequireBytes(metadata, 0x24,
        [
            6, 1, 15, 0, 0, 0, 1, 2,
            6, 2, 15, 1, 0, 0, 2, 2
        ], "pixel attribute mapping");
        RequireWord(metadata, 0x134, 2, "pixel perspective-center barycentrics");
        RequireWord(metadata, 0x138, 0x10, "pixel depth and coverage control");
        ValidateHalfExport(metadata, code);
    }

    private static void ValidateHalfExport(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> code)
    {
        RequireWord(metadata, 0x148, 6, "pixel export patch length");
        RequireWord(metadata, ExportPatchOffset, ExportInstructionOffset / 4, "pixel export patch offset");

        for (var renderTarget = 1; renderTarget < 8; renderTarget++)
        {
            RequireWord(metadata, ExportPatchOffset + renderTarget * ExportPatchBytes, uint.MaxValue,
                "additional pixel render target");
        }

        ReadOnlySpan<uint> halfExport =
        [
            0xbf800000, 0xbf800000,
            0x5e000702, 0x5e020b04,
            0xf8001c0f, 0x00000100
        ];

        RequireExtent(code, ExportInstructionOffset + halfExport.Length * 4, "pixel export code");

        var variantOffset = ExportPatchOffset + 4 + HalfExportVariant * 40;

        for (var instructionIndex = 0; instructionIndex < halfExport.Length; instructionIndex++)
        {
            var instructionOffset = instructionIndex * 4;

            RequireWord(metadata, variantOffset + instructionOffset, halfExport[instructionIndex],
                "pixel FP16 export patch");
            RequireWord(code, ExportInstructionOffset + instructionOffset, halfExport[instructionIndex],
                "pixel FP16 export instructions");
        }
    }

    private static void RequireExtent(ReadOnlySpan<byte> data, int bytes, string field)
    {
        if (data.Length < bytes)
        {
            throw new InvalidDataException($"Planar-color {field} metadata is truncated or unavailable.");
        }
    }

    private static void RequireWord(ReadOnlySpan<byte> data, int offset, uint expected, string field)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) != expected)
        {
            throw new NotSupportedException($"Unsupported planar-color {field}.");
        }
    }

    private static void RequireBytes(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> expected, string field)
    {
        if (!data.Slice(offset, expected.Length).SequenceEqual(expected))
        {
            throw new NotSupportedException($"Unsupported planar-color {field}.");
        }
    }

    internal static void VerifyAuthoredContracts()
    {
        var vertex = new byte[VertexMetadataBytes];
        var pixel = new byte[PixelMetadataBytes];
        var code = new byte[ExportInstructionOffset + 24];

        Convert.FromHexString("0600070402060103080106020F0C03").CopyTo(vertex, 0x24);
        Convert.FromHexString("060100000000060201000000").CopyTo(vertex, 0xc8);
        BinaryPrimitives.WriteUInt32LittleEndian(vertex.AsSpan(0xc4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(vertex.AsSpan(0x188), 2);

        Convert.FromHexString("06010F000000010206020F0100000202").CopyTo(pixel, 0x24);
        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(0x124), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(0x134), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(0x138), 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(0x148), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(ExportPatchOffset), ExportInstructionOffset / 4);

        for (var renderTarget = 1; renderTarget < 8; renderTarget++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(ExportPatchOffset + renderTarget * ExportPatchBytes),
                uint.MaxValue);
        }

        var export = Convert.FromHexString("000080BF000080BF0207005E040B025E0F1C00F800010000");

        export.CopyTo(pixel, ExportPatchOffset + 4 + HalfExportVariant * 40);
        export.CopyTo(code, ExportInstructionOffset);

        ValidateVertex(vertex);
        ValidatePixel(pixel, code);

        vertex[0x27] = 8;

        try
        {
            ValidateVertex(vertex);
            throw new InvalidOperationException("Planar semantic remapping was accepted.");
        }
        catch (NotSupportedException)
        {
        }

        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(0x134), 0x20);

        try
        {
            ValidatePixel(pixel, code);
            throw new InvalidOperationException("Planar non-perspective barycentrics were accepted.");
        }
        catch (NotSupportedException)
        {
        }

        BinaryPrimitives.WriteUInt32LittleEndian(pixel.AsSpan(0x134), 2);
        code[ExportInstructionOffset + 20] = 2;

        try
        {
            ValidatePixel(pixel, code);
            throw new InvalidOperationException("Planar export source mutation was accepted.");
        }
        catch (NotSupportedException)
        {
        }

        try
        {
            ValidatePixel(pixel.AsSpan(0, PixelMetadataBytes - 1), code);
            throw new InvalidOperationException("Truncated planar metadata was accepted.");
        }
        catch (InvalidDataException)
        {
        }
    }
}
