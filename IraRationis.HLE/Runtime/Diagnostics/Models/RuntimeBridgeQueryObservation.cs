// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Diagnostics.Models;

internal readonly record struct RuntimeBridgeQueryObservation(
    long Id,
    RuntimeBridgeQueryKey Key,
    RuntimeBridgeQuerySeries? Series,
    long Sequence,
    long StartedTicks,
    nint Output,
    bool BeginRecorded);
