// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media;

namespace IraRationis.Cli;

internal static class MediaProbeCommand
{
    internal static int Execute(string[] args, TextWriter writer)
    {
        if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] is not ("--play" or "--stream")))
        {
            PrintUsage(writer);

            return 2;
        }

        var native = args[0] == "--mf";

        if (args.Length == 3 && args[2] == "--stream")
        {
            if (native)
            {
                PrintUsage(writer);

                return 2;
            }

            return Stream(args[0], args[1], writer);
        }

        var output = Path.Combine("artifacts", "media-probe", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var clip = native
            ? MediaFoundationDecoder.Decode(args[1])
            : LocalMediaDecoder.DecodeAsync(args[0], args[1]).GetAwaiter().GetResult();

        var report = MediaClipInspector.Inspect(clip, output);
        var revision = native ? "IRX-R37" : "IRX-R34";
        var backend = native ? "MediaFoundation" : "FFmpeg";

        writer.WriteLine($"[media] {revision}: frames={report.Frames}; unique={report.DistinctFrames}; " +
                         $"audioSamples={report.AudioSamples}; peak={report.Peak}; sampleRate={report.SampleRate}; " +
                         $"channels={report.Channels}; backend={backend}");
        writer.WriteLine($"[media] Output: {report.OutputDirectory}");

        if (args.Length != 3)
        {
            return 0;
        }

        var playback = MediaPlaybackSession.Play(clip);

        writer.WriteLine($"[media] IRX-R35: presented={playback.PresentedFrames}; skipped={playback.SkippedFrames}; " +
                         $"cancelled={playback.Cancelled}; audioSeconds={playback.AudioSeconds:F3}");

        return playback.Cancelled ? 2 : 0;
    }

    private static int Stream(string decoder, string path, TextWriter writer)
    {
        var streaming = StreamingMediaSession.Play(decoder, path);

        writer.WriteLine($"[media] IRX-R36: decoded={streaming.DecodedFrames}; presented={streaming.PresentedFrames}; "
                         +
                         $"skipped={streaming.SkippedFrames}; cancelled={streaming.Cancelled}; " +
                         $"audioSeconds={streaming.AudioSeconds:F3}; queueCapacity={StreamingMediaSession.FrameQueueCapacity}");

        return streaming.Cancelled ? 2 : 0;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: irx media-probe <ffmpeg.exe> <local clip> [--play|--stream]");
        writer.WriteLine("       irx media-probe --mf <local MP4> [--play]");
    }
}
