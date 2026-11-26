// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Decoding.Models;

public sealed record MediaInspection(
    int Frames,
    int DistinctFrames,
    int AudioSamples,
    int Peak,
    int SampleRate,
    int Channels,
    string OutputDirectory);
