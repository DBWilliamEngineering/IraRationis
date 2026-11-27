// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.HLE.Runtime.Diagnostics;

namespace IraRationis.HLE.Audio.Movie;

internal static class MovieAudioReadinessProbe
{
    private const int TrackEnabledOffset = 0x44;
    private const int TrackRingOffset = 0x6C;
    private const int TrackSuspendedOffset = 0x80;
    private const int RingCapacityOffset = 0x0C;
    private const int RingQueueCountOffset = 0x34;
    private const int RingFirstQueueOffset = 0x38;
    private const int QueueReadOffset = 0x00;
    private const int QueueWriteOffset = 0x04;
    private const int QueueBytesOffset = 0x08;

    internal static MovieAudioReadinessObservation Capture(nint channel, uint blockBytes,
        out MovieAudioQueueSample sample)
    {
        sample = default;

        Span<byte> track = stackalloc byte[TrackSuspendedOffset + sizeof(uint)];

        if (!RuntimeBridgeMemoryReader.TryReadSnapshot(channel, track))
        {
            return MovieAudioReadinessObservation.Unavailable;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(track[TrackSuspendedOffset..]) != 0)
        {
            return MovieAudioReadinessObservation.Suspended;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(track[TrackEnabledOffset..]) == 0)
        {
            return MovieAudioReadinessObservation.Disabled;
        }

        var ringAddress = ReadGuestPointer(track, TrackRingOffset);
        Span<byte> ring = stackalloc byte[RingFirstQueueOffset + sizeof(ulong)];

        if (!RuntimeBridgeMemoryReader.TryReadSnapshot(ringAddress, ring) ||
            BinaryPrimitives.ReadUInt32LittleEndian(ring[RingQueueCountOffset..]) != 1)
        {
            return MovieAudioReadinessObservation.Unavailable;
        }

        var capacity = BinaryPrimitives.ReadUInt32LittleEndian(ring[RingCapacityOffset..]);

        if (blockBytes == 0 || capacity / 2 != blockBytes || (capacity & 1) != 0)
        {
            return MovieAudioReadinessObservation.Unavailable;
        }

        var queueAddress = ReadGuestPointer(ring, RingFirstQueueOffset);
        Span<byte> queue = stackalloc byte[QueueBytesOffset + sizeof(uint)];

        if (!RuntimeBridgeMemoryReader.TryReadSnapshot(queueAddress, queue))
        {
            return MovieAudioReadinessObservation.Unavailable;
        }

        var queued = BinaryPrimitives.ReadUInt32LittleEndian(queue[QueueBytesOffset..]);

        if (queued > capacity)
        {
            return MovieAudioReadinessObservation.Unavailable;
        }

        var readOffset = BinaryPrimitives.ReadUInt32LittleEndian(queue[QueueReadOffset..]);
        var writeOffset = BinaryPrimitives.ReadUInt32LittleEndian(queue[QueueWriteOffset..]);
        sample = new MovieAudioQueueSample(capacity, queued, readOffset, writeOffset);

        // The guest mixer can change occupancy after Ready returns.
        return capacity - queued < blockBytes
            ? MovieAudioReadinessObservation.QueueBelowRequiredSpace
            : MovieAudioReadinessObservation.SpaceAvailableAfterReturn;
    }

    private static nint ReadGuestPointer(ReadOnlySpan<byte> state, int offset)
    {
        return unchecked((nint)BinaryPrimitives.ReadUInt64LittleEndian(state[offset..]));
    }
}
