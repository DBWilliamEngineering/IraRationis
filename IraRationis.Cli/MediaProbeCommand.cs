// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media;

namespace IraRationis.Cli;

internal static class MediaProbeCommand
{
    internal static int Execute(string[] args, TextWriter writer)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--play" or "--stream")))
        {
            writer.WriteLine("Usage: irx media-probe <ffmpeg.exe> <local clip> [--play|--stream]");

            return 2;
        }

        if (args.Length == 3 && args[2] == "--stream")
        {
            try
            {
                var streaming = StreamingMediaSession.Play(args[0], args[1]);
                writer.WriteLine(
                    $"[media] IRX-R36: decoded={streaming.DecodedFrames}; presented={streaming.PresentedFrames}; " +
                    $"skipped={streaming.SkippedFrames}; cancelled={streaming.Cancelled}; audioSeconds={streaming.AudioSeconds:F3}; "
                    +
                    $"queueCapacity={StreamingMediaSession.FrameQueueCapacity}");

                return streaming.Cancelled ? 2 : 0;
            }
            catch (TimeoutException exception)
            {
                writer.WriteLine($"[media] {exception.Message}");

                return 1;
            }
        }

        var output = Path.Combine("artifacts", "media-probe", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var clip = LocalMediaDecoder.DecodeAsync(args[0], args[1]).GetAwaiter().GetResult();
        var report = MediaClipInspector.Inspect(clip, output);
        writer.WriteLine($"[media] IRX-R34: frames={report.Frames}; unique={report.DistinctFrames}; " +
                         $"audioSamples={report.AudioSamples}; peak={report.Peak}; sampleRate={report.SampleRate}; channels={report.Channels}");
        writer.WriteLine($"[media] Output: {report.OutputDirectory}");

        if (args.Length == 3)
        {
            PlaybackResult playback;
            try
            {
                playback = MediaPlaybackSession.Play(clip);
            }
            catch (TimeoutException exception)
            {
                writer.WriteLine($"[media] {exception.Message}");

                return 1;
            }

            writer.WriteLine(
                $"[media] IRX-R35: presented={playback.PresentedFrames}; skipped={playback.SkippedFrames}; " +
                $"cancelled={playback.Cancelled}; audioSeconds={playback.AudioSeconds:F3}");

            return playback.Cancelled ? 2 : 0;
        }

        return 0;
    }
}
