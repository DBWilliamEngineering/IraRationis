// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Media.Decoding;
using IraRationis.Media.Playback;

namespace IraRationis.Cli.Commands;

internal static class MediaProbeCommand
{
    internal static int Execute(string[] args, TextWriter writer)
    {
        if (args.Length is < 2 or > 3
            || (args.Length == 3 && args[2] is not ("--play" or "--stream" or "--gpu" or "--stream-gpu")))
        {
            PrintUsage(writer);

            return 2;
        }

        var native = args[0] == "--mf";

        if (args.Length == 3 && args[2] is "--stream" or "--stream-gpu")
        {
            if (native)
            {
                PrintUsage(writer);

                return 2;
            }

            return Stream(args[0], args[1], writer, args[2] == "--stream-gpu");
        }

        var output = Path.Combine("artifacts", "media-probe", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var clip = native
            ? MediaFoundationDecoder.Decode(args[1])
            : LocalMediaDecoder.DecodeAsync(args[0], args[1]).GetAwaiter().GetResult();

        if (args.Length == 3 && args[2] == "--gpu")
        {
            // Skip CPU thumbnail generation and inspection in this explicit GPU playback mode.
            var result =
                GpuMediaPlaybackSession.Play(clip,
                    (window, width, height) => new ProbeGpuPresenter(window, width, height, writer));

            writer.WriteLine($"[media] IRX-R43: presented={result.PresentedFrames}; skipped={result.SkippedFrames}; " +
                             $"cancelled={result.Cancelled}; audioSeconds={result.AudioSeconds:F3}; sourceFrames={clip.FrameCount}.");

            if (result.Cancelled)
            {
                return 2;
            }

            return result.PresentedFrames == 0 ? 1 : 0;
        }

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

    private static int Stream(string decoder, string path, TextWriter writer, bool gpu)
    {
        var streaming = gpu
            ? StreamingMediaSession.PlayGpu(decoder, path,
                (window, width, height) => new ProbeGpuPresenter(window, width, height, writer))
            : StreamingMediaSession.Play(decoder, path);

        writer.WriteLine($"[media] IRX-R36: decoded={streaming.DecodedFrames}; presented={streaming.PresentedFrames}; "
                         +
                         $"skipped={streaming.SkippedFrames}; cancelled={streaming.Cancelled}; " +
                         $"audioSeconds={streaming.AudioSeconds:F3}; queueCapacity={StreamingMediaSession.FrameQueueCapacity}");

        return streaming.Cancelled ? 2 : streaming.PresentedFrames == 0 ? 1 : 0;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: irx media-probe <ffmpeg.exe> <local clip> [--play|--stream|--gpu|--stream-gpu]");

        writer.WriteLine("       irx media-probe --mf <local MP4> [--play|--gpu]");
    }
}
