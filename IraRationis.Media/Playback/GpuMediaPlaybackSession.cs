// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using IraRationis.Media.Audio;
using IraRationis.Media.Decoding.Models;
using IraRationis.Media.Playback.Models;
using IraRationis.Media.Presentation;

namespace IraRationis.Media.Playback;

/// <summary>Bounded local media playback, synchronized to the PCM device clock.</summary>
public static class GpuMediaPlaybackSession
{
    public static PlaybackResult Play(DecodedClip clip, Func<nint, int, int, IGpuVideoPresenter> createPresenter)
    {
        ArgumentNullException.ThrowIfNull(clip);

        ArgumentNullException.ThrowIfNull(createPresenter);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (clip.Width is < 2 or > 1920 || clip.Height is < 2 or > 1080 ||
            (clip.Width & 1) != 0 || (clip.Height & 1) != 0 ||
            clip.Nv12.Length == 0 || clip.Nv12.Length > 128 * 1024 * 1024 ||
            clip.Nv12.Length % clip.FrameBytes != 0 || clip.FrameCount > 150 ||
            clip.SampleRate != 48000 || clip.Channels != 2 || clip.Pcm.Length == 0 ||
            clip.Pcm.Length % 2 != 0 || clip.Pcm.Length > 48000 * 2 * 5)
        {
            throw new InvalidDataException("Invalid bounded playback clip.");
        }

        var duration = clip.Pcm.Length / (double)(clip.SampleRate * clip.Channels);

        if (Math.Abs(duration - clip.FrameCount / 30.0) > 1.0 / 30.0)
        {
            throw new InvalidDataException("Video and PCM durations disagree by more than one frame.");
        }

        var diagnostics = Environment.GetEnvironmentVariable("IRX_HOST_MEDIA_DIAGNOSTICS") == "1";

        if (diagnostics)
        {
            var peak = 0;

            foreach (var sample in clip.Pcm)
            {
                peak = Math.Max(peak, Math.Abs((int)sample));
            }

            Report($"clip accepted; frames={clip.FrameCount}; size={clip.Width}x{clip.Height}; " +
                   $"pcmSamples={clip.Pcm.Length}; peak={peak}.");
        }

        var stage = "window";

        try
        {
            return PlayCore(clip, createPresenter, duration, diagnostics, ref stage);
        }
        catch (Exception exception)
        {
            if (diagnostics)
            {
                Report($"failed; stage={stage}; error={exception.GetType().Name}; " +
                       $"HRESULT=0x{exception.HResult:X8}.");

                Report(exception.ToString());
            }

            throw;
        }
    }

    private static PlaybackResult PlayCore(DecodedClip clip,
        Func<nint, int, int, IGpuVideoPresenter> createPresenter, double duration,
        bool diagnostics, ref string stage)
    {
        using var window = new WindowsVideoWindow(clip.Width, clip.Height);

        stage = "presenter initialization";

        using var presenter = createPresenter(window.Handle, clip.Width, clip.Height);

        stage = "audio initialization";

        using var audio = new WindowsPcmClock(clip);

        if (diagnostics)
        {
            Report("window, GPU presenter and audio clock initialized.");
        }

        var timeline = new HostFrameTimeline(clip.FrameCount);

        stage = "first frame window pump";

        if (!window.Pump())
        {
            stage = "cancellation and disposal";

            return new PlaybackResult(0, 0, true, 0);
        }

        // Keep audio stopped during the initial submission. A slow first Present
        // must not cause the opening image to be skipped by the audio clock.
        var firstStarted = Stopwatch.GetTimestamp();

        stage = "first frame timeline selection";

        if (!timeline.TrySelect(0, out var first))
        {
            throw new InvalidOperationException("Unable to select the opening frame.");
        }

        stage = "first frame retrieval";

        var pixels = clip.GetFrame(first);

        if (diagnostics)
        {
            Report($"preroll input; index={first}; frameCount={clip.FrameCount}; " +
                   $"bytes={pixels.Length}; expectedBytes={clip.FrameBytes}; " +
                   $"size={clip.Width}x{clip.Height}.");
        }

        stage = "first frame GPU presenter";

        var accepted = presenter.Present(pixels);

        stage = "first frame timeline accounting";

        timeline.Record(accepted);

        if (diagnostics)
        {
            Report(FormattableString.Invariant(
                $"preroll; accepted={accepted}; index={first}; ms={Stopwatch.GetElapsedTime(firstStarted).TotalMilliseconds:F3}; content unverified."));
        }

        if (!accepted)
        {
            throw new InvalidOperationException("Opening frame was not accepted; audio not started.");
        }

        stage = "post-preroll window pump";

        if (!window.Pump())
        {
            stage = "cancellation and disposal";

            return new PlaybackResult(timeline.Presented, timeline.Skipped, true, 0);
        }

        stage = "audio start";

        var timer = Stopwatch.StartNew();

        audio.Start();

        stage = "playback";

        var clockAdvanced = false;

        if (diagnostics)
        {
            Report("audio start returned successfully after first frame acceptance.");
        }

        while (window.Pump())
        {
            if (timer.Elapsed.TotalSeconds > duration + 5)
            {
                throw new TimeoutException("GPU playback exceeded its bounded deadline.");
            }

            stage = "audio clock read";

            var done = audio.Done;
            var seconds = audio.Seconds;

            stage = "playback timeline selection";

            var selected = timeline.TrySelect(seconds, out var index);

            if (diagnostics && !clockAdvanced && timeline.Position > 0)
            {
                clockAdvanced = true;

                Report(FormattableString.Invariant($"audio clock advanced; seconds={timeline.Position:F3}."));
            }

            if (selected)
            {
                stage = "playback frame retrieval";

                var frame = clip.GetFrame(index);

                stage = "playback GPU presenter";

                var frameAccepted = presenter.Present(frame);

                stage = "playback timeline accounting";

                timeline.Record(frameAccepted);
            }

            if (done)
            {
                timeline.Finish();

                stage = "completion and disposal";

                if (diagnostics)
                {
                    Report(FormattableString.Invariant(
                        $"completed; accepted={timeline.Presented}; skipped={timeline.Skipped}; audioSeconds={timeline.Position:F3}; expectedSeconds={duration:F3}; audible output unverified."));
                }

                return new PlaybackResult(timeline.Presented, timeline.Skipped, false, timeline.Position);
            }

            stage = "playback window pump";

            Thread.Sleep(2);
        }

        stage = "cancellation and disposal";

        if (diagnostics)
        {
            Report(FormattableString.Invariant(
                $"cancelled; accepted={timeline.Presented}; skipped={timeline.Skipped}; audioSeconds={timeline.Position:F3}."));
        }

        return new PlaybackResult(timeline.Presented, timeline.Skipped, true, timeline.Position);
    }

    private static void Report(string message)
    {
        try
        {
            Console.Error.WriteLine($"[media] IRX-R48-HOST: {message}");
        }
        catch (Exception)
        {
            // Diagnostic output is best effort and cannot alter playback results.
        }
    }
}
