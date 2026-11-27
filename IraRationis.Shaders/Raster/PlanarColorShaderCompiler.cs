// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Shaders.Containers;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Native.Models;
using IraRationis.Shaders.Raster.Models;
using IraRationis.Shaders.Raster.NativeInstructions;
using IraRationis.Shaders.Raster.PlanarColor;

namespace IraRationis.Shaders.Raster;

public static class PlanarColorShaderCompiler
{
    public static PlanarColorShaderPair Translate(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> pixel,
        ReadOnlyMemory<byte> root)
    {
        var vertexContainer = ShaderContainer.Parse(vertex);
        var pixelContainer = ShaderContainer.Parse(pixel);

        PlanarShaderContract.ValidateSignatures(vertexContainer, pixelContainer);
        PlanarShaderContract.ValidateRoot(ShaderContainer.Parse(root));

        var vertexProgram = ReadProgram(vertexContainer, NativeShaderStage.Vertex, 16, 38);
        var pixelProgram = ReadProgram(pixelContainer, NativeShaderStage.Pixel, 12, 42);

        PlanarNativeMetadata.Validate(vertexProgram, pixelProgram);
        PlanarShaderContract.ValidateResources(vertexProgram, pixelProgram);
        PlanarInstructionContract.ValidateVertex(vertexProgram.Code);
        PlanarInstructionContract.ValidatePixel(pixelProgram.Code);

        return new PlanarColorShaderPair(PlanarColorShaderSource.Vertex, PlanarColorShaderSource.Pixel);
    }

    public static void VerifyAuthoredContracts()
    {
        RasterInstructionReader.VerifyAuthoredContracts();
        PlanarShaderContract.VerifyAuthoredContracts();
        PlanarNativeMetadata.VerifyAuthoredContracts();
    }

    private static NativeProgram ReadProgram(ShaderContainer container, NativeShaderStage stage,
        uint vectorRegisters, uint scalarRegisters)
    {
        var programs = NativeProgramReader.Read(container);

        if (programs.Count != 1 || programs[0].Stage != stage || !programs[0].Wave64 ||
            programs[0].LocalBytes != 0 || programs[0].VectorRegisters != vectorRegisters ||
            programs[0].ScalarRegisters != scalarRegisters)
        {
            throw new NotSupportedException($"Unsupported planar-color {stage} stage or register contract.");
        }

        return programs[0];
    }
}
