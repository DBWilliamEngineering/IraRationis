// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media.Audio;

namespace RuntimeMedia;

internal sealed unsafe class ProgramProbeSink(WindowsPcmMonitor? monitor) : IPcmPacketSink
{
    public int Packets
    {
        get;
        private set;
    }

    public int Frames
    {
        get;
        private set;
    }

    public int Peak
    {
        get;
        private set;
    }

    public short[] LastPacket
    {
        get;
        private set;
    } = [];

    public bool TrySubmit(ReadOnlySpan<short> samples, int channels)
    {
        LastPacket = samples.ToArray();

        Packets++;

        Frames += samples.Length / channels;

        foreach (var sample in samples)
        {
            Peak = Math.Max(Peak, Math.Abs((int)sample));
        }

        return monitor?.TrySubmit(samples, channels) ?? true;
    }
}
