// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Raster.NativeInstructions;

namespace IraRationis.Shaders.Raster.DualMask;

internal static class DualMaskNativeContract
{
    private const int ResourceRecordBytes = 20;
    private const int UsedSamplerSlot = 2;
    private const uint UsedSamplerRegister = 14;
    private const int ExportPatchOffset = 0x14c;
    private const int ExportPatchStride = 404;
    private const int ExportCodeOffset = 0x130;
    private const int HalfExportVariant = 4;

    internal static bool HasBindingShape(NativeProgram pixel)
    {
        var resources = pixel.UsedResources.AsSpan();

        return resources.Length == 4 * ResourceRecordBytes &&
               BinaryPrimitives.ReadUInt32LittleEndian(resources) == 0x401 &&
               BinaryPrimitives.ReadUInt32LittleEndian(resources[ResourceRecordBytes..]) == 0x300 &&
               BinaryPrimitives.ReadUInt32LittleEndian(resources[(2 * ResourceRecordBytes)..]) == 0x300 &&
               BinaryPrimitives.ReadUInt32LittleEndian(resources[(3 * ResourceRecordBytes)..]) == 0x302;
    }

    internal static void Validate(NativeProgram vertex, NativeProgram pixel)
    {
        ValidateRootLayout(vertex.RootLayout);
        ValidateRootLayout(pixel.RootLayout);
        Require(vertex.UsedResources.Length == 0, "vertex resources");
        RequireWords(pixel.UsedResources, 0,
        [
            0x401, 0, 14, 0, 0x20004,
            0x300, 0, 257, 4, 0x808,
            0x300, 8, 0, 4, 0x808,
            0x302, 16, 27, 0, 0x804
        ], "pixel resources");
        Require(pixel.UsedResources.Length == 80, "pixel resource count");

        ValidateVertexMetadata(vertex.StageMetadata);
        ValidatePixelMetadata(pixel.StageMetadata, pixel.Code);
        ValidatePixelCode(pixel.Code);
    }

    private static void ValidateRootLayout(ReadOnlySpan<byte> layout)
    {
        const int tableRecordCount = 3;
        var samplerCount = layout.Length / ResourceRecordBytes - tableRecordCount;
        Require(layout.Length % ResourceRecordBytes == 0 && samplerCount is > UsedSamplerSlot and <= 64,
            "root layout length");

        for (var samplerIndex = 0; samplerIndex < samplerCount; samplerIndex++)
        {
            var offset = samplerIndex * ResourceRecordBytes;
            var register = BinaryPrimitives.ReadUInt32LittleEndian(layout[(offset + 12)..]);
            Require(samplerIndex != UsedSamplerSlot || register == UsedSamplerRegister, "sampled sampler register");
            RequireWords(layout, offset, [4, 0, 0, register, (uint)samplerIndex << 8], "static sampler root record");
        }

        RequireWords(layout, samplerCount * ResourceRecordBytes,
            [3, 0, 1, 257, 8, 3, 8, 1, 0, 8, 3, 16, 1, 27, 8], "descriptor table root records");
    }

    private static void ValidateVertexMetadata(ReadOnlySpan<byte> metadata)
    {
        Require(metadata.Length == 0x18c, "vertex metadata length");
        RequireWords(metadata, 0xc4, [3], "vertex input count");
        Require(metadata.Slice(0x24, 15).SequenceEqual<byte>(
            [6, 0, 7, 4, 2, 6, 1, 15, 8, 3, 6, 2, 3, 12, 1]), "vertex input-to-VGPR mapping");
        RequireWords(metadata, 0x188, [2], "vertex output count");
        Require(metadata.Slice(0xc8, 12).SequenceEqual<byte>(
            [6, 1, 0, 0, 0, 0, 6, 2, 1, 0, 0, 0]), "vertex parameter export mapping");
    }

    private static void ValidatePixelMetadata(ReadOnlySpan<byte> metadata, ReadOnlySpan<byte> code)
    {
        Require(metadata.Length == ExportPatchOffset + 8 * ExportPatchStride, "pixel metadata length");
        RequireWords(metadata, 0x124, [1], "pixel input count");
        Require(metadata.Slice(0x24, 8).SequenceEqual<byte>([6, 2, 3, 0, 0, 0, 2, 2]), "pixel TEXCOORD attribute");
        RequireWords(metadata, 0x134, [2, 0x10], "perspective-center input and depth/coverage control");
        RequireWords(metadata, 0x148, [6, ExportCodeOffset / 4], "export patch length and offset");

        for (var renderTarget = 1; renderTarget < 8; renderTarget++)
        {
            RequireWords(metadata, ExportPatchOffset + renderTarget * ExportPatchStride, [uint.MaxValue],
                "additional pixel render target");
        }

        const int variantOffset = ExportPatchOffset + 4 + HalfExportVariant * 40;
        const int exportCodeBytes = 6 * sizeof(uint);

        Require(code.Length >= ExportCodeOffset + exportCodeBytes, "export code length");
        Require(metadata.Slice(variantOffset, exportCodeBytes).SequenceEqual(code.Slice(ExportCodeOffset, exportCodeBytes)),
            "matching FP16 export patch");
    }

    private static void ValidatePixelCode(ReadOnlySpan<byte> code)
    {
        var reader = new RasterInstructionReader(code);
        const uint zero = 128;
        const uint one = 242;
        const uint scalarLiteral = 255;
        const uint firstMask = 40;
        const uint secondMask = 20;
        const uint sampler = 0;
        const uint coordinates = 2;

        reader.IdentityHeader();
        reader.Prefetch(3);
        reader.Move32(2, 9);
        reader.Move32(9, zero);
        reader.Move32(124, 2);
        reader.LoadDescriptor(firstMask, 8, 8, 0);
        reader.LoadDescriptor(secondMask, 8, 8, 0x20);
        reader.Interpolate(3, 0, 1, false);
        reader.Interpolate(2, 0, 0, false);
        reader.Move32(2, scalarLiteral, 0x18f00000);
        reader.Move32(1, scalarLiteral, 0x00c00000);
        reader.MoveSmallInteger(0, 0x0400);
        reader.Interpolate(3, 0, 1, true);
        reader.Interpolate(2, 0, 0, true);
        reader.Move32(3, zero);
        reader.Wait(0xc07f);
        reader.SampleLodZeroAlpha(1, firstMask, sampler, coordinates);
        reader.SampleLodZeroAlpha(0, secondMask, sampler, coordinates);
        reader.VectorNoOperation();
        reader.LoadDescriptor(0, 8, 4, 0x40);
        reader.Log2(2, zero);
        reader.Wait(0xc07f);
        reader.LoadBuffer(0, 0, 4, 0x30);
        reader.Wait(0xc07f);

        const uint vector0 = 256;
        const uint vector1 = 257;
        const uint vector2 = 258;
        const uint vector3 = 259;
        const uint vector5 = 261;
        const uint vector6 = 262;
        const uint vector7 = 263;

        reader.Reciprocal(3, 2);
        reader.Reciprocal(4, 3);
        reader.MultiplyLegacy(3, vector2, 3);
        reader.Exp2(3, vector3);
        reader.MultiplyLegacy(2, vector2, 4);
        reader.Exp2(2, vector2);
        reader.Subtract(5, one, 3);
        reader.Reciprocal(6, vector5);
        reader.Subtract(7, one, 2);
        reader.Log2(5, vector5);
        reader.FusedMultiplyAdd(3, vector3, vector6, 1);
        reader.Log2(6, vector7);
        reader.Maximum(3, zero, 3);
        reader.MultiplyLegacy(5, 2, 5);
        reader.Log2(3, vector3);
        reader.MultiplyLegacy(6, 3, 6);
        reader.Exp2(5, vector5);
        reader.Exp2(6, vector6);
        reader.MultiplyLegacy(3, 2, 3);
        reader.Exp2(3, vector3);
        reader.Reciprocal(6, vector6);
        reader.MultiplyLegacy(3, vector3, 5);
        reader.Reciprocal(5, vector7);
        reader.MultiplyLegacy(3, vector3, 6);
        reader.Maximum(3, zero, 3);
        reader.FusedMultiplyAdd(2, vector2, vector5, 0, negations: 5);
        reader.Log2(3, vector3);
        reader.MultiplyLegacy(3, vector3, 4);
        reader.Exp2(3, vector3);
        reader.Add(2, vector2, 3);
        reader.MoveVector32(3, one);
        reader.Wait(0x3f71);
        reader.FusedMultiplyAdd(2, vector1, vector2, 0);
        reader.Wait(0x3f70);
        reader.AccumulateNegatedProduct(2, vector0, vector2);
        reader.ScalarNoOperation();
        reader.ScalarNoOperation();
        reader.PackHalfTowardZero(0, vector2, 2);
        reader.PackHalfTowardZero(1, vector2, 3);
        reader.Export(0, 0, 1, 0, 0, done: true, compressed: true, validMask: true);
        reader.End();
        reader.Padding();
    }

    private static void RequireWords(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<uint> expected, string contract)
    {
        Require(offset >= 0 && offset <= bytes.Length && expected.Length <= (bytes.Length - offset) / sizeof(uint), contract);

        for (var index = 0; index < expected.Length; index++)
        {
            Require(BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + index * sizeof(uint))..]) == expected[index],
                contract);
        }
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition)
        {
            throw new NotSupportedException($"Unsupported dual-mask rectangle {contract}.");
        }
    }
}
