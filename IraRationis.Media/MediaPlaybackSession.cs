// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace IraRationis.Media;

public sealed record PlaybackResult(int PresentedFrames, int SkippedFrames, bool Cancelled, double AudioSeconds);

/// <summary>Plays a bounded, constant-30-FPS decoded local clip with audio as the master clock.</summary>
public static class MediaPlaybackSession
{
    public static PlaybackResult Play(DecodedClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

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

        using var window = new WindowsVideoWindow(clip.Width, clip.Height);
        using var audio = new WindowsPcmClock(clip);

        var pixels = new byte[checked(clip.Width * clip.Height * 4)];
        var ySize = clip.Width * clip.Height;
        var last = -1;
        var presented = 0;
        var skipped = 0;
        var position = 0.0;
        var timer = Stopwatch.StartNew();

        audio.Start();

        while (window.Pump())
        {
            if (timer.Elapsed.TotalSeconds > duration + 5)
            {
                throw new TimeoutException("Audio playback did not complete within the bounded deadline.");
            }

            var done = audio.Done;

            position = Math.Max(position, audio.Seconds);

            var index = Math.Min(clip.FrameCount - 1, (int)(position * 30));

            if (index != last)
            {
                var frame = clip.GetFrame(index);

                Nv12Converter.Convert(frame[..ySize], clip.Width, frame[ySize..], clip.Width,
                    clip.Width, clip.Height, pixels, clip.Width * 4);

                skipped += index - last - 1;

                last = index;

                if (window.Draw(pixels))
                {
                    presented++;
                }
            }
            else
            {
                // Repaint after resize or uncover even if the device clock has not advanced.
                window.Draw(pixels);
            }

            if (done)
            {
                return new PlaybackResult(presented, skipped, false, position);
            }

            Thread.Sleep(5);
        }

        return new PlaybackResult(presented, skipped, true, position);
    }
}
