/**
 * @file        BcPackBc1Dictionary.cs
 * @description Stores verified BC1 endpoint and selector dictionary entries.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     Uniform quadrant codes are built in. Optional entries are copied into an immutable dictionary.
 *
 * See the repository license and attribution notices.
 */

namespace IraRationis.HLE.Storage.BcPack;

internal sealed class BcPackBc1Dictionary
{
    private const int HistorySize = 255;
    private const int SelectorCount = 256;
    private const int UniformSelectorCount = 4;
    private readonly uint?[] _endpointHistory;
    private readonly byte?[] _quadrantCodes;

    private BcPackBc1Dictionary(uint?[] endpointHistory, byte?[] quadrantCodes)
    {
        _endpointHistory = endpointHistory;
        _quadrantCodes = quadrantCodes;
    }

    internal static BcPackBc1Dictionary Empty { get; } = new([], []);

    internal static bool TryCreate(ReadOnlySpan<uint?> endpointHistory, ReadOnlySpan<byte?> quadrantCodes,
        out BcPackBc1Dictionary? dictionary)
    {
        dictionary = null;
        if (endpointHistory.Length != HistorySize || quadrantCodes.Length != SelectorCount)
        {
            return false;
        }

        Span<bool> used = stackalloc bool[SelectorCount];
        used.Clear();
        for (var index = 0; index < UniformSelectorCount; index++)
        {
            var code = UniformQuadrantCode(index);
            if (quadrantCodes[index] is { } configured && configured != code)
            {
                return false;
            }

            used[code] = true;
        }

        foreach (var entry in quadrantCodes[UniformSelectorCount..])
        {
            if (entry is not { } code)
            {
                continue;
            }

            if (used[code])
            {
                return false;
            }

            used[code] = true;
        }

        dictionary = new BcPackBc1Dictionary(endpointHistory.ToArray(), quadrantCodes.ToArray());
        return true;
    }

    internal bool TryGetEndpointPair(int distanceBeforeStart, out uint endpoints)
    {
        var index = distanceBeforeStart - 1;
        if ((uint)index < (uint)_endpointHistory.Length && _endpointHistory[index] is { } entry)
        {
            endpoints = entry;
            return true;
        }

        endpoints = 0;
        return false;
    }

    internal bool TryGetQuadrantCode(byte selector, out byte code)
    {
        if (selector < UniformSelectorCount)
        {
            code = UniformQuadrantCode(selector);
            return true;
        }

        if (selector < _quadrantCodes.Length && _quadrantCodes[selector] is { } entry)
        {
            code = entry;
            return true;
        }

        code = 0;
        return false;
    }

    private static byte UniformQuadrantCode(int selector)
    {
        return (byte)((selector ^ 2) * 0x55);
    }
}
