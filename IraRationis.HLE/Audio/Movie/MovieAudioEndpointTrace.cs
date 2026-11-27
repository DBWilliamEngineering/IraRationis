// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using IraRationis.HLE.Runtime.Diagnostics;

namespace IraRationis.HLE.Audio.Movie;

internal sealed class MovieAudioEndpointTrace
{
    private const int SpatialOutputOffset = 0x3CED0;
    private const int SpatialActiveOffset = 0x3DD84;
    private const int StreamCountOffset = 0x40;
    private const int ActivatedStreamCountOffset = 0x4FC;
    private const int FirstStreamOffset = 0x500;
    private const int FirstEventOffset = 0x518;
    private const int FirstMixRingOffset = 0x530;
    private const int MixRingCapacityOffset = 0x0C;
    private const int MixRingLaneCountOffset = 0x34;
    private const int MixRingLanesOffset = 0x38;
    private const int LaneQueuedBytesOffset = 0x08;
    private const uint WaitSignaled = 0;
    private const uint WaitTimeout = 258;

    private nint _mixerState;
    private nint _bufferEvent;
    private long _signals;
    private long _timeouts;
    private long _failures;
    private long _lastSignal;
    private long _maxSignalGapTicks;

    internal bool TryAttach(nint mixerState)
    {
        return mixerState > 0 && Interlocked.CompareExchange(ref _mixerState, mixerState, 0) == 0;
    }

    internal void Detach(nint mixerState)
    {
        if (Interlocked.CompareExchange(ref _mixerState, 0, mixerState) == mixerState)
        {
            Volatile.Write(ref _bufferEvent, 0);
            Interlocked.Exchange(ref _lastSignal, 0);
        }
    }

    internal bool Observes(nint handle)
    {
        return handle != 0 && handle == Volatile.Read(ref _bufferEvent);
    }

    internal void RecordWait(uint result)
    {
        if (result == WaitTimeout)
        {
            Interlocked.Increment(ref _timeouts);
            return;
        }

        if (result != WaitSignaled)
        {
            Interlocked.Increment(ref _failures);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastSignal, now);

        if (previous != 0)
        {
            var gap = now - previous;
            for (var maximum = Volatile.Read(ref _maxSignalGapTicks); gap > maximum;
                 maximum = Volatile.Read(ref _maxSignalGapTicks))
            {
                if (Interlocked.CompareExchange(ref _maxSignalGapTicks, gap, maximum) == maximum)
                {
                    break;
                }
            }
        }

        Interlocked.Increment(ref _signals);
    }

    internal string? TakeReport(long now)
    {
        var state = Volatile.Read(ref _mixerState);
        if (state == 0)
        {
            return null;
        }

        Span<byte> active = stackalloc byte[1];
        if (!TryReadAt(state, SpatialActiveOffset, active) || active[0] > 1)
        {
            Volatile.Write(ref _bufferEvent, 0);
            return "spatialOutput=unavailable";
        }

        if (active[0] == 0)
        {
            Volatile.Write(ref _bufferEvent, 0);
            return "spatialOutput=inactive";
        }

        var output = state + SpatialOutputOffset;
        Span<byte> count = stackalloc byte[sizeof(uint)];
        Span<byte> stream = stackalloc byte[FirstEventOffset - ActivatedStreamCountOffset + sizeof(long)];

        if (!TryReadAt(output, StreamCountOffset, count) ||
            !TryReadAt(output, ActivatedStreamCountOffset, stream) || ReadUInt32(count, 0) != 1 ||
            ReadUInt32(stream, 0) != 1 || ReadPointer(stream, FirstStreamOffset - ActivatedStreamCountOffset) == 0)
        {
            Volatile.Write(ref _bufferEvent, 0);
            return "spatialOutput=unavailable-stream-layout";
        }

        var bufferEvent = ReadPointer(stream, FirstEventOffset - ActivatedStreamCountOffset);
        if (bufferEvent == 0)
        {
            Volatile.Write(ref _bufferEvent, 0);
            return "spatialOutput=unavailable-event";
        }

        if (Interlocked.Exchange(ref _bufferEvent, bufferEvent) != bufferEvent)
        {
            Interlocked.Exchange(ref _lastSignal, 0);
        }

        var ring = ReadMixRing(output);
        var lastSignal = Interlocked.Read(ref _lastSignal);
        var lastSignalAge = lastSignal == 0 ? -1 : Stopwatch.GetElapsedTime(lastSignal, Math.Max(lastSignal, now)).TotalMilliseconds;
        var maxGap = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _maxSignalGapTicks)).TotalMilliseconds;

        return string.Create(CultureInfo.InvariantCulture,
            $"spatialOutput=active; endpointSignals={Interlocked.Read(ref _signals)}; endpointTimeouts={Interlocked.Read(ref _timeouts)}; " +
            $"endpointWaitFailures={Interlocked.Read(ref _failures)}; endpointMaxSignalGapMs={maxGap:F2}; " +
            $"endpointLastSignalAgeMs={lastSignalAge:F2}; {ring}");
    }

    private static string ReadMixRing(nint output)
    {
        Span<byte> ring = stackalloc byte[MixRingLanesOffset + sizeof(long)];
        Span<byte> lane = stackalloc byte[LaneQueuedBytesOffset + sizeof(uint)];

        if (TryReadAt(output, FirstMixRingOffset, ring) && ReadUInt32(ring, MixRingLaneCountOffset) != 0 &&
            RuntimeBridgeMemoryReader.TryReadSnapshot(ReadPointer(ring, MixRingLanesOffset), lane))
        {
            var capacity = ReadUInt32(ring, MixRingCapacityOffset);
            var queued = ReadUInt32(lane, LaneQueuedBytesOffset);
            if (capacity is > 0 and <= 64 * 1024 && queued <= capacity)
            {
                return string.Create(CultureInfo.InvariantCulture, $"mixedLane0QueuedBytes={queued}; mixedLane0CapacityBytes={capacity}");
            }
        }

        return "mixedLane0=unavailable";
    }

    private static bool TryReadAt(nint address, int offset, Span<byte> destination)
    {
        return address > 0 && address <= nint.MaxValue - offset &&
            RuntimeBridgeMemoryReader.TryReadSnapshot(address + offset, destination);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> state, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(state[offset..]);
    }

    private static nint ReadPointer(ReadOnlySpan<byte> state, int offset)
    {
        return unchecked((nint)BinaryPrimitives.ReadUInt64LittleEndian(state[offset..]));
    }
}
