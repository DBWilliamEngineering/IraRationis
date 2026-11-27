// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Decoding.Models;

internal sealed record MediaFoundationDecoderAudioPayload(long Start, short[] Samples);
