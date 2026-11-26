// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media.Audio;

namespace IraRationis.Cli.Diagnostics;

internal sealed class CountingPcmSink(WindowsPcmMonitor monitor) : IPcmPacketSink
{
    private long _accepted;
    private long _frames;
    private long _observed;

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
