/*
 * @file        BcPackEntropyReader.cs
 * @description Schedules shared compressed bytes among BCPack entropy lanes.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     The feeder services up to eight lanes in index order per round and emits one complete symbol group at a time.
 *
 * See the repository license and attribution notices.
 */

namespace IraRationis.HLE.Storage.BcPack;

internal ref struct BcPackEntropyReader
{
    private const int ReadsPerRound = 8;
    // Texture pool chunk metadata stores the alignment padding count in 12 bits.
    private const int MaximumTrailingPadding = 4095;

    private readonly ReadOnlySpan<byte> _source;
    private readonly Span<BcPackEntropyLane> _lanes;
    private readonly int _expectedUnits;
    private readonly long _maximumRounds;
    private long _rounds;
    private int _offset;
    private int _unitsRead;

    internal BcPackEntropyReader(ReadOnlySpan<byte> source, Span<BcPackEntropyLane> lanes,
        scoped ReadOnlySpan<byte> widths, int expectedUnits)
    {
        _source = source;
        _lanes = lanes[..widths.Length];
        _expectedUnits = expectedUnits;
        // O(compressed bytes + output units) with a fixed number of lanes per format.
        _maximumRounds = expectedUnits * 20L + source.Length + 200;

        for (var lane = 0; lane < widths.Length; lane++)
        {
            _lanes[lane] = new BcPackEntropyLane(widths[lane]);
        }
    }

    internal bool TryReadUnit(scoped Span<byte> symbols)
    {
        if (_unitsRead >= _expectedUnits || symbols.Length != _lanes.Length)
        {
            return false;
        }

        while (!AllLanesHaveSymbols())
        {
            if (!AdvanceRound())
            {
                return false;
            }
        }

        for (var lane = 0; lane < _lanes.Length; lane++)
        {
            symbols[lane] = _lanes[lane].TakeSymbol();
        }

        _unitsRead++;

        return true;
    }

    internal bool TryFinish()
    {
        if (_unitsRead != _expectedUnits)
        {
            return false;
        }

        while (!AllLanesComplete())
        {
            if (!AdvanceRound())
            {
                return false;
            }
        }

        var padding = _source[_offset..];

        return padding.Length <= MaximumTrailingPadding && padding.IndexOfAnyExcept((byte)0) < 0;
    }

    private bool AdvanceRound()
    {
        if (_rounds++ >= _maximumRounds)
        {
            return false;
        }

        var stalledLanes = 0;
        var bytesServed = 0;

        for (var index = 0; index < _lanes.Length; index++)
        {
            ref var lane = ref _lanes[index];

            if (!lane.NeedsByte)
            {
                continue;
            }

            if (bytesServed == ReadsPerRound)
            {
                stalledLanes |= 1 << index;

                continue;
            }

            if (_offset == _source.Length || !lane.AcceptByte(_source[_offset++]))
            {
                return false;
            }

            bytesServed++;
        }

        for (var index = 0; index < _lanes.Length; index++)
        {
            if (!_lanes[index].Advance((stalledLanes & (1 << index)) != 0, _expectedUnits))
            {
                return false;
            }
        }

        return true;
    }

    private readonly bool AllLanesHaveSymbols()
    {
        foreach (ref readonly var lane in _lanes)
        {
            if (!lane.HasSymbol)
            {
                return false;
            }
        }

        return true;
    }

    private readonly bool AllLanesComplete()
    {
        foreach (ref readonly var lane in _lanes)
        {
            if (!lane.IsComplete)
            {
                return false;
            }
        }

        return true;
    }
}
