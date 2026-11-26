// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media;

/// <summary>
///     Receives interleaved 48 kHz PCM16. The span is valid only during the call.
///     Implementations must copy retained data and must not block the producer.
/// </summary>
public interface IPcmPacketSink
{
    bool TrySubmit(ReadOnlySpan<short> samples, int channels);
}
