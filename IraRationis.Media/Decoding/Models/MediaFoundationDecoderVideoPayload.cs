// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Decoding.Models;

internal sealed record MediaFoundationDecoderVideoPayload(
    int Width,
    int Height,
    int FrameBytes,
    long Start,
    byte[] Frames);
