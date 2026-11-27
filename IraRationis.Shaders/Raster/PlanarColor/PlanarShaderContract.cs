// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;
using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;

namespace IraRationis.Shaders.Raster.PlanarColor;

internal static class PlanarShaderContract
{
    internal static void ValidateSignatures(ShaderContainer vertex, ShaderContainer pixel)
    {
        ValidateSignature(vertex, "ISG1",
        [
            ("POSITION", 0U, 0U, 7U, 7U),
            ("TEXCOORD", 0U, 1U, 3U, 3U),
            ("COLOR", 0U, 2U, 15U, 15U)
        ]);
        ValidateSignature(vertex, "OSG1",
        [
            ("SV_Position", 1U, 0U, 15U, 0U),
            ("COLOR", 0U, 1U, 15U, 0U),
            ("TEXCOORD", 0U, 2U, 15U, 0U)
        ]);
        ValidateSignature(pixel, "ISG1",
        [
            ("SV_Position", 1U, 0U, 15U, 0U),
            ("COLOR", 0U, 1U, 15U, 15U),
            ("TEXCOORD", 0U, 2U, 15U, 15U)
        ]);
        ValidateSignature(pixel, "OSG1", [("SV_Target", 64U, 0U, 15U, 0U)]);
    }

    internal static void ValidateRoot(ShaderContainer root)
    {
        var data = root.Part("RTS0")?.Data ?? throw new NotSupportedException("Planar-color root signature is missing.");

        RequireWords(data.Span, [2, 1, 24, 0, 68, 1, 0, 0, 36, 1, 44, 2, 1, 27, 1, 1, uint.MaxValue],
            "root signature envelope");
    }

    internal static void ValidateResources(NativeProgram vertex, NativeProgram pixel)
    {
        ReadOnlySpan<uint> rootLayout =
        [
            4, 0x00, 0, 3, 0,
            3, 0x00, 1, 2, 8,
            3, 0x08, 1, 6, 8,
            3, 0x10, 1, 5, 8,
            3, 0x18, 1, 7, 8,
            3, 0x20, 1, 10, 8,
            3, 0x28, 1, 0, 8,
            3, 0x30, 1, 3, 8
        ];

        RequireWords(vertex.RootLayout, rootLayout, "vertex root layout");
        RequireWords(pixel.RootLayout, rootLayout, "pixel root layout");
        RequireWords(vertex.UsedResources,
        [
            0x300, 0x00, 2, 0x40, 0x804,
            0x302, 0x28, 0, 0x00, 0x804,
            0x302, 0x30, 3, 0x00, 0x804
        ], "vertex resource usage");
        RequireWords(pixel.UsedResources,
        [
            0x401, 0x00, 3, 0x00, 0x004,
            0x300, 0x08, 6, 0x04, 0x808,
            0x300, 0x10, 5, 0x04, 0x808,
            0x300, 0x18, 7, 0x04, 0x808,
            0x300, 0x20, 10, 0x04, 0x808,
            0x302, 0x30, 3, 0x00, 0x804
        ], "pixel resource usage");
    }

    private static void ValidateSignature(ShaderContainer container, string tag,
        ReadOnlySpan<(string Name, uint System, uint Register, uint Mask, uint UsedMask)> expected)
    {
        var data = (container.Part(tag) ?? throw new NotSupportedException($"Planar-color {tag} signature is missing.")).Data.Span;
        var tableEnd = 8 + 32 * expected.Length;

        if (data.Length < tableEnd || Word(data, 0) != (uint)expected.Length || Word(data, 4) != 8)
        {
            throw new NotSupportedException($"Unsupported planar-color {tag} signature shape.");
        }

        for (var elementIndex = 0; elementIndex < expected.Length; elementIndex++)
        {
            var offset = 8 + 32 * elementIndex;
            var element = expected[elementIndex];
            var nameOffset = Word(data, offset + 4);

            if (nameOffset < tableEnd || nameOffset >= data.Length)
            {
                throw new InvalidDataException($"Planar-color {tag} semantic name is outside its string region.");
            }

            var nameBytes = data[(int)nameOffset..];
            var terminator = nameBytes.IndexOf((byte)0);

            if (terminator < 0 || !nameBytes[..terminator].SequenceEqual(Encoding.ASCII.GetBytes(element.Name)) ||
                Word(data, offset) != 0 || Word(data, offset + 8) != 0 || Word(data, offset + 12) != element.System ||
                Word(data, offset + 16) != 3 || Word(data, offset + 20) != element.Register ||
                data[offset + 24] != element.Mask || data[offset + 25] != element.UsedMask ||
                data[offset + 26] != 0 || data[offset + 27] != 0 || Word(data, offset + 28) != 0)
            {
                throw new NotSupportedException($"Unsupported planar-color {tag} semantic {elementIndex}.");
            }
        }
    }

    private static void RequireWords(ReadOnlySpan<byte> actual, ReadOnlySpan<uint> expected, string contract)
    {
        if (actual.Length != expected.Length * 4)
        {
            throw new NotSupportedException($"Planar-color {contract} length differs.");
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (Word(actual, 4 * index) != expected[index])
            {
                throw new NotSupportedException($"Planar-color {contract} differs at word {index}.");
            }
        }
    }

    private static uint Word(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    internal static void VerifyAuthoredContracts()
    {
        var rootBytes = new byte[68];
        ReadOnlySpan<uint> rootWords = [2, 1, 24, 0, 68, 1, 0, 0, 36, 1, 44, 2, 1, 27, 1, 1, uint.MaxValue];

        for (var index = 0; index < rootWords.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(rootBytes.AsSpan(index * 4), rootWords[index]);
        }

        var root = new ShaderContainer(new byte[16], 1, 0, [new ShaderPart("RTS0", 0, rootBytes)]);

        ValidateRoot(root);

        rootBytes[52] = 28;

        try
        {
            ValidateRoot(root);
            throw new InvalidOperationException("Planar root-register mutation was accepted.");
        }
        catch (NotSupportedException)
        {
        }
    }
}
