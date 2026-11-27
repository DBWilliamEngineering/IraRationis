/*
* @file        BcPackBc1Decoder.cs
* @description Reconstructs BC1 blocks from validated BCPack symbol streams.
* @author      Damon William
* @date        2026-11-27
*
* @details     The caller supplies staging storage and publishes it only after the complete stream is validated.
*
* See the repository license and attribution notices.
*/

using System.Buffers.Binary;

namespace IraRationis.HLE.Storage.BcPack;

internal static class BcPackBc1Decoder
{
    private const int BlockBytes = 8;
    private const int EndpointBytes = 4;
    private const int LaneCount = 12;

    // Destination is staging storage. Publish it only after the stream closes successfully.
    internal static bool TryDecode(ReadOnlySpan<byte> source, Span<byte> destination,
        BcPackBc1Dictionary? dictionary = null)
    {
        if (source.IsEmpty || destination.IsEmpty || destination.Length % BlockBytes != 0)
        {
            return false;
        }

        dictionary ??= BcPackBc1Dictionary.Empty;
        ReadOnlySpan<byte> widths = [8, 8, 5, 6, 5, 5, 6, 5, 8, 8, 8, 8];
        var blockCount = destination.Length / BlockBytes;
        Span<BcPackEntropyLane> lanes = stackalloc BcPackEntropyLane[LaneCount];
        Span<byte> symbols = stackalloc byte[LaneCount];
        var reader = new BcPackEntropyReader(source, lanes, widths, blockCount);
        for (var block = 0; block < blockCount; block++)
        {
            if (!reader.TryReadUnit(symbols) || !TryReconstructBlock(symbols, destination, block, dictionary))
            {
                return false;
            }
        }

        return reader.TryFinish();
    }

    private static bool TryReconstructBlock(ReadOnlySpan<byte> symbols, Span<byte> destination, int block,
        BcPackBc1Dictionary dictionary)
    {
        if (!TryResolvePredictor(symbols[0], destination, block, dictionary, out var predictor) ||
            !TryResolveSelectors(symbols, dictionary, out var selectors))
        {
            return false;
        }

        var output = destination.Slice(block * BlockBytes, BlockBytes);
        var first = ApplyResiduals((ushort)predictor, symbols[2..5]);
        var second = ApplyResiduals((ushort)(predictor >> 16), symbols[5..8]);
        BinaryPrimitives.WriteUInt16LittleEndian(output, first);
        BinaryPrimitives.WriteUInt16LittleEndian(output[2..], second);
        BinaryPrimitives.WriteUInt32LittleEndian(output[EndpointBytes..], selectors);
        return true;
    }

    private static bool TryResolvePredictor(byte symbol, ReadOnlySpan<byte> destination, int block,
        BcPackBc1Dictionary dictionary, out uint predictor)
    {
        predictor = 0;
        if (symbol == 0)
        {
            return true;
        }

        var distance = (symbol & 1) == 0 ? symbol >> 1 : byte.MaxValue - (symbol >> 1);
        if (distance > block)
        {
            return dictionary.TryGetEndpointPair(distance - block, out predictor);
        }

        predictor = BinaryPrimitives.ReadUInt32LittleEndian(destination[((block - distance) * BlockBytes)..]);
        return true;
    }

    private static bool TryResolveSelectors(ReadOnlySpan<byte> symbols, BcPackBc1Dictionary dictionary,
        out uint selectors)
    {
        selectors = 0;
        if (symbols[1] != 0 ||
            !dictionary.TryGetQuadrantCode(symbols[8], out var topLeft) ||
            !dictionary.TryGetQuadrantCode(symbols[9], out var topRight) ||
            !dictionary.TryGetQuadrantCode(symbols[10], out var bottomLeft) ||
            !dictionary.TryGetQuadrantCode(symbols[11], out var bottomRight))
        {
            return false;
        }

        selectors = InterleaveRows(topLeft, topRight) | ((uint)InterleaveRows(bottomLeft, bottomRight) << 16);
        return true;
    }

    private static ushort InterleaveRows(byte left, byte right)
    {
        return (ushort)((left & 0x0f) | ((right & 0x0f) << 4) | ((left & 0xf0) << 4) | ((right & 0xf0) << 8));
    }

    private static ushort ApplyResiduals(ushort endpoint, ReadOnlySpan<byte> symbols)
    {
        var red = ((endpoint >> 11) + DecodeResidual(symbols[0])) & 31;
        var green = (((endpoint >> 5) & 63) + DecodeResidual(symbols[1])) & 63;
        var blue = ((endpoint & 31) + DecodeResidual(symbols[2])) & 31;
        return (ushort)((red << 11) | (green << 5) | blue);
    }

    private static int DecodeResidual(byte symbol)
    {
        return (symbol >> 1) ^ -(symbol & 1);
    }
}
