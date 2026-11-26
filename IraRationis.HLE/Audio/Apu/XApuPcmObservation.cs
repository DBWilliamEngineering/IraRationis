// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media.Audio;

namespace IraRationis.HLE.Audio.Apu;

internal sealed class XApuPcmObservation(uint stream, IPcmPacketSink sink) : IDisposable
{
    private readonly Lock _gate = new();
    private bool _closed;
    private XApuConnection? _selected;

    public void Dispose()
    {
        lock (_gate)
        {
            _closed = true;

            _selected = null;
        }

        _ = Interlocked.CompareExchange(ref XApuModule._pcmObservation, null, this);
    }

    internal void Observe(XApuConnection connection, uint index, ReadOnlySpan<short> samples, int channels)
    {
        if (index != stream)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _selected ??= connection;

            if (!ReferenceEquals(_selected, connection))
            {
                return;
            }

            try
            {
                _ = sink.TrySubmit(samples, channels);
            }
            catch (Exception)
            {
                // A failing diagnostic sink cannot change the guest ABI contract.
                _closed = true;

                _selected = null;
            }
        }
    }
}
