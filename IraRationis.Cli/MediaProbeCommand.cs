// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media;

namespace IraRationis.Cli;

internal static class MediaProbeCommand
{
    internal static int Execute(string[] args, TextWriter writer)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--play"))
        {
            writer.WriteLine("Usage: irx media-probe <ffmpeg.exe> <local clip> [--play]");

            return 2;
        }

        var output = Path.Combine("artifacts", "media-probe", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var clip = LocalMediaDecoder.DecodeAsync(args[0], args[1]).GetAwaiter().GetResult();
        var report = MediaClipInspector.Inspect(clip, output);
        writer.WriteLine($"[media] IRX-R34: frames={report.Frames}; unique={report.DistinctFrames}; " +
                         $"audioSamples={report.AudioSamples}; peak={report.Peak}; sampleRate={report.SampleRate}; channels={report.Channels}");
        writer.WriteLine($"[media] Output: {report.OutputDirectory}");

        if (args.Length == 3)
        {
            WindowsWaveOutput.Play(clip.Pcm, clip.SampleRate, clip.Channels);
            writer.WriteLine("[media] Windows synchronous playback completed.");
        }

        return 0;
    }
}
