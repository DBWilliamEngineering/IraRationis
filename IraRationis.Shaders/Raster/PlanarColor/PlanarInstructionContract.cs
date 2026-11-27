// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Shaders.Raster.NativeInstructions;

namespace IraRationis.Shaders.Raster.PlanarColor;

internal static class PlanarInstructionContract
{
    internal static void ValidateVertex(ReadOnlySpan<byte> code)
    {
        var reader = new RasterInstructionReader(code);

        reader.IdentityHeader();
        reader.SwapProgramCounter(4, 4);
        reader.Prefetch(3);
        reader.Move32(9, 128);
        reader.LoadDescriptor(32, 8, 4, 0);
        reader.LoadDescriptor(16, 8, 4, 0xc0);
        reader.LoadDescriptor(0, 8, 4, 0xa0);
        reader.Wait(0xc07f);
        reader.LoadBuffer(0, 0, 16, 0x160);

        for (uint row = 0; row < 4; row++)
        {
            reader.LoadBuffer(20 + 4 * row, 32, 4, row * 16);
        }

        reader.Wait(0x0070);

        for (uint component = 0; component < 3; component++)
        {
            reader.FusedMultiplyAdd(component, 20 + component, RasterInstructionReader.Vector(4), 32 + component);
        }

        for (uint component = 0; component < 3; component++)
        {
            reader.Accumulate(component, 24 + component, 5);
        }

        for (uint component = 0; component < 3; component++)
        {
            reader.Accumulate(component, 28 + component, 6);
        }

        ReadOnlySpan<uint> clipRegisters = [3, 4, 7, 0];

        for (var component = 0; component < clipRegisters.Length; component++)
        {
            reader.MultiplyLegacy(clipRegisters[component], (uint)component, 0);
        }

        reader.LoadBuffer(0, 16, 4, 0x60);

        for (uint row = 1; row <= 2; row++)
        {
            for (var component = 0; component < clipRegisters.Length; component++)
            {
                reader.Accumulate(clipRegisters[component], 4 * row + (uint)component, row);
            }
        }

        reader.Add(3, 12, 3);
        reader.Add(10, 13, 4);
        reader.Add(5, 14, 7);
        reader.Add(0, 15, 0);
        reader.Export(12, 3, 10, 5, 0, done: true);
        reader.Wait(0xc07f);
        reader.MultiplyLegacy(1, 0, 8);
        reader.MultiplyLegacy(4, 1, 9);
        reader.MultiplyLegacy(6, 2, 8);
        reader.MultiplyLegacy(7, 3, 9);
        reader.Export(32, 12, 13, 14, 15);
        reader.Export(33, 1, 4, 6, 7);
        reader.End();
        reader.Padding();
    }

    internal static void ValidatePixel(ReadOnlySpan<byte> code)
    {
        var reader = new RasterInstructionReader(code);

        reader.IdentityHeader();
        reader.Prefetch(3);
        reader.Move64(10, 126);
        reader.WholeQuadMask(126, 126);
        reader.Move32(2, 9);
        reader.Move32(9, 128);
        reader.Move32(124, 2);
        reader.LoadDescriptor(12, 8, 8, 0x60);
        reader.LoadDescriptor(20, 8, 8, 0x20);
        reader.LoadDescriptor(28, 8, 8, 0x40);
        reader.LoadDescriptor(4, 8, 4, 0xc0);

        ReadOnlySpan<uint> coordinateRegisters = [2, 3, 4, 7];

        for (var phase = 0; phase < 2; phase++)
        {
            for (var component = 0; component < coordinateRegisters.Length; component++)
            {
                reader.Interpolate(coordinateRegisters[component], 1, (uint)component, phase != 0);
            }
        }

        reader.And64(126, 126, 10);
        reader.MoveSmallInteger(36, 0x0092);
        reader.Move32(37, 255, 0x00c00000);
        reader.Move32(38, 255, 0x14500000);
        reader.Move32(39, 128);
        reader.Interpolate(8, 0, 1, false);
        reader.Wait(0xc07f);
        reader.SamplePlane(2, 28, 36, 2, 3);
        reader.SamplePlane(3, 20, 36, 4, 7);
        reader.SamplePlane(4, 12, 36, 4, 7);
        reader.LoadBuffer(0, 4, 1, 0x80);
        reader.VectorNoOperation();
        reader.LoadBuffer(12, 4, 16, 0);
        reader.Interpolate(7, 0, 0, false);
        reader.Interpolate(11, 0, 2, false);
        reader.Interpolate(8, 0, 1, true);
        reader.Interpolate(7, 0, 0, true);
        reader.Interpolate(11, 0, 2, true);
        reader.Wait(0x0072);
        reader.Maximum(2, 0, 2);
        reader.LoadBuffer(0, 4, 1, 0x70);
        reader.MultiplyLegacy(5, 24, 2);
        reader.MultiplyLegacy(6, 25, 2);
        reader.MultiplyLegacy(2, 26, 2);
        reader.Wait(0x3f71);
        reader.Accumulate(5, 12, 3);
        reader.Accumulate(6, 13, 3);
        reader.Accumulate(2, 14, 3);
        reader.Wait(0x3f70);
        reader.Accumulate(5, 16, 4);
        reader.Accumulate(6, 17, 4);
        reader.Accumulate(2, 18, 4);
        reader.Add(10, 20, 5);
        reader.Add(3, 21, 6);
        reader.Add(5, 22, 2);
        reader.Wait(0xc07f);
        reader.CompareEqual(0, 128);
        reader.MultiplyLegacy(2, RasterInstructionReader.Vector(10), 7);
        reader.MultiplyLegacy(3, RasterInstructionReader.Vector(3), 8);
        reader.MultiplyLegacy(4, RasterInstructionReader.Vector(5), 11);
        reader.BranchIfEqual(5);
        reader.LoadDescriptor(4, 8, 8, 0x80);
        reader.Wait(0xc07f);
        reader.SampleLookup(2, 4, 36, 2);
        reader.Move32(124, 2);
        reader.Interpolate(5, 0, 3, false);
        reader.Interpolate(5, 0, 3, true);
        reader.Wait(0x3f70);
        reader.ScalarNoOperation();
        reader.ScalarNoOperation();
        reader.PackHalfTowardZero(0, RasterInstructionReader.Vector(2), 3);
        reader.PackHalfTowardZero(1, RasterInstructionReader.Vector(4), 5);
        reader.Export(0, 0, 1, 0, 0, done: true, compressed: true, validMask: true);
        reader.End();
        reader.Padding();
    }
}
