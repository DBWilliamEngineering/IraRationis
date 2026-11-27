// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Audio.Movie;

internal readonly record struct MovieAudioQueueSample(uint Capacity, uint Queued, uint ReadOffset, uint WriteOffset)
{
    internal bool HasCoherentCursors
    {
        get
        {
            return Capacity != 0 && ReadOffset < Capacity && WriteOffset < Capacity &&
                ((ulong)WriteOffset + Capacity - ReadOffset) % Capacity == Queued % Capacity;
        }
    }
}
