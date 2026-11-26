// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace IraRationis.Media;

/// <summary>Host-owned presentation contract; implementations must not retain borrowed frame bytes.</summary>
public interface IGpuVideoPresenter : IDisposable
{
    bool Present(ReadOnlySpan<byte> nv12);
}

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
        var last = -1;
        var presented = 0;
        var skipped = 0;
        var position = 0.0;
        var timer = Stopwatch.StartNew();

        stage = "audio start";
        audio.Start();
        stage = "playback";
        var clockAdvanced = false;
        if (diagnostics)
        {
            Report("audio start returned successfully.");
        }

        while (window.Pump())
        {
            if (timer.Elapsed.TotalSeconds > duration + 5)
            {
                throw new TimeoutException("GPU playback exceeded its bounded deadline.");
            }

            var done = audio.Done;
            position = Math.Max(position, audio.Seconds);
            if (diagnostics && !clockAdvanced && position > 0)
            {
                clockAdvanced = true;
                Report(FormattableString.Invariant($"audio clock advanced; seconds={position:F3}."));
            }

            var index = Math.Min(clip.FrameCount - 1, (int)(position * 30));
            if (index != last)
            {
                skipped += index - last - 1;
                last = index;
                if (presenter.Present(clip.GetFrame(index)))
                {
                    presented++;
                    if (diagnostics && presented == 1)
                    {
                        Report($"first frame accepted by presenter; index={index}; content unverified.");
                    }
                }
                else
                {
                    skipped++;
                }
            }

            if (done)
            {
                skipped += clip.FrameCount - 1 - last;

                stage = "completion and disposal";
                if (diagnostics)
                {
                    Report(FormattableString.Invariant($"completed; accepted={presented}; skipped={skipped}; audioSeconds={position:F3}; expectedSeconds={duration:F3}; audible output unverified."));
                }

                return new PlaybackResult(presented, skipped, false, position);
            }

            Thread.Sleep(2);
        }

        stage = "cancellation and disposal";
        if (diagnostics)
        {
            Report(FormattableString.Invariant($"cancelled; accepted={presented}; skipped={skipped}; audioSeconds={position:F3}."));
        }

        return new PlaybackResult(presented, skipped, true, position);
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
