// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Decoding.Models;

internal sealed unsafe record MediaFoundationDecoderAudioPayload(long Start, short[] Samples);
