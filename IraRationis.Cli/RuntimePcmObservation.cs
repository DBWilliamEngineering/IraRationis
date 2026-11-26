// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using IraRationis.HLE;
using IraRationis.Media;

namespace IraRationis.Cli;

/// <summary>Owns the optional single-voice PCM diagnostic for one runtime launch.</summary>
internal sealed class RuntimePcmObservation : IDisposable
{
    private readonly WindowsPcmMonitor _monitor;
    private readonly CountingSink _sink;
    private IDisposable? _registration;

    private RuntimePcmObservation(WindowsPcmMonitor monitor, CountingSink sink, IDisposable registration)
    {
        _monitor = monitor;
        _sink = sink;
        _registration = registration;
    }

    internal static RuntimePcmObservation? Start()
    {
        var value = Environment.GetEnvironmentVariable("IRX_RUNTIME_PCM_STREAM");

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var stream) || stream >= 500)
        {
            throw new ArgumentException("IRX_RUNTIME_PCM_STREAM must be a voice index from 0 to 499.");
        }

        Console.Error.WriteLine($"[audio] IRX-R41: diagnostic PCM observer enabled for stream={stream}; single voice, not a mixer.");

        var monitor = new WindowsPcmMonitor();
        var sink = new CountingSink(monitor);

        try
        {
            var registration = XApuModule.BeginPcmObservation(stream, sink);

            return new RuntimePcmObservation(monitor, sink, registration);
        }
        catch
        {
            monitor.Dispose();
            _ = monitor.WaitForCompletion(TimeSpan.FromSeconds(8));

            throw;
        }
    }

    public void Dispose()
    {
        var registration = Interlocked.Exchange(ref _registration, null);

        if (registration is null)
        {
            return;
        }

        // Stop observing before closing the sink. The registration waits for any
        // in-flight diagnostic callback, and does not acquire a connection lock.
        registration.Dispose();
        _monitor.Dispose();

        var drained = _monitor.WaitForCompletion(TimeSpan.FromSeconds(8));

        Console.Error.WriteLine(
            $"[audio] IRX-R41: observed={_sink.Observed}; frames={_sink.Frames}; " +
            $"accepted={_sink.Accepted}; completed={_monitor.CompletedPackets}; " +
            $"drained={drained}; faulted={_monitor.Faulted}.");
    }

    private sealed class CountingSink(WindowsPcmMonitor monitor) : IPcmPacketSink
    {
        private long _observed;
        private long _frames;
        private long _accepted;

        internal long Observed
        {
            get
            {
                return Interlocked.Read(ref _observed);
            }
        }

        internal long Frames
        {
            get
            {
                return Interlocked.Read(ref _frames);
            }
        }

        internal long Accepted
        {
            get
            {
                return Interlocked.Read(ref _accepted);
            }
        }

        public bool TrySubmit(ReadOnlySpan<short> samples, int channels)
        {
            Interlocked.Increment(ref _observed);
            Interlocked.Add(ref _frames, samples.Length / channels);

            if (!monitor.TrySubmit(samples, channels))
            {
                return false;
            }

            Interlocked.Increment(ref _accepted);

            return true;
        }
    }
}
