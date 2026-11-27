// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using Concentus.Structs;

namespace IraRationis.HLE.Audio.Apu.Models;

internal sealed class XApuVoice(OpusDecoder decoder, int channels)
{
    public readonly int Channels = channels;
    public readonly OpusDecoder Decoder = decoder;
}
