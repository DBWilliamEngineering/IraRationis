// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media;

namespace IraRationis.HLE;

public static partial class XApuModule
{
    private static PcmObservation? _pcmObservation;

    /// <summary>
    ///     Registers a host-owned, nonblocking observer for one connection and voice.
    ///     Pass null to detach. The caller owns the sink's lifetime.
    /// </summary>
    public static bool AttachPcmSink(nint handle, uint stream, IPcmPacketSink? sink)
    {
        if (!Connections.TryGetValue(handle, out var connection))
        {
            return false;
        }

        lock (connection.Gate)
        {
            if (connection.Closed || stream >= (uint)connection.Voices.Length)
            {
                return false;
            }

            connection.PcmStream = stream;
            connection.PcmSink = sink;

            return true;
        }
    }

    /// <summary>
    ///     Observes one explicitly selected stream on the first connection that supplies
    ///     valid PCM. This is a diagnostic voice tap, not a mixer. Dispose before
    ///     disposing the caller-owned sink. Do not re-enter XApu from sink callbacks.
    /// </summary>
    public static IDisposable BeginPcmObservation(uint stream, IPcmPacketSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(stream, 500U);

        var observation = new PcmObservation(stream, sink);

        if (Interlocked.CompareExchange(ref _pcmObservation, observation, null) is not null)
        {
            throw new InvalidOperationException("A runtime PCM observation is already active.");
        }

        return observation;
    }

    private static void ObservePcm(Connection connection, uint stream, ReadOnlySpan<short> samples, int channels)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        var sink = connection.PcmSink;

        if (sink is null)
        {
            Volatile.Read(ref _pcmObservation)?.Observe(connection, stream, samples, channels);

            return;
        }

        if (connection.PcmStream != stream)
        {
            return;
        }

        try
        {
            _ = sink.TrySubmit(samples, channels);
        }
        catch (Exception)
        {
            // Diagnostics must not change the guest result or unwind across the unmanaged ABI.
            connection.PcmSink = null;
        }
    }

    private sealed class PcmObservation(uint stream, IPcmPacketSink sink) : IDisposable
    {
        private readonly Lock _gate = new();
        private bool _closed;
        private Connection? _selected;

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _selected = null;
            }

            _ = Interlocked.CompareExchange(ref _pcmObservation, null, this);
        }

        internal void Observe(Connection connection, uint index, ReadOnlySpan<short> samples, int channels)
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
}
