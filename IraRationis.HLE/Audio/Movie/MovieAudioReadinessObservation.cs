// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Audio.Movie;

internal enum MovieAudioReadinessObservation
{
    Unavailable,
    Suspended,
    Disabled,
    QueueBelowRequiredSpace,
    SpaceAvailableAfterReturn,
    Count
}
