// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Playback.Models;

public sealed record StreamingPlaybackResult(
    int DecodedFrames,
    int PresentedFrames,
    int SkippedFrames,
    bool Cancelled,
    double AudioSeconds);
