// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using IraRationis.Media.Audio;
using IraRationis.Media.Decoding.Models;
using IraRationis.Media.Video;

namespace IraRationis.Media.Decoding;

/// <summary>Writes bounded, independent media inspection results. No guest callbacks or runtime state.</summary>
public static class MediaClipInspector
{
    public static MediaInspection Inspect(DecodedClip clip, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(clip);

        if (clip.Width is < 2 or > 1920 || clip.Height is < 2 or > 1080 ||
            (clip.Width & 1) != 0 || (clip.Height & 1) != 0 ||
            clip.Nv12.Length == 0 || clip.Nv12.Length % clip.FrameBytes != 0 ||
            clip.Nv12.Length > 128 * 1024 * 1024 || clip.Pcm.Length > 48000 * 2 * 5 ||
            clip.SampleRate != 48000 || clip.Channels != 2)
        {
            throw new InvalidDataException("Invalid bounded media clip.");
        }

        var wave = PcmMixer.ToWave(clip.Pcm, clip.SampleRate, clip.Channels);
        var directory = Path.GetFullPath(outputDirectory);

        Directory.CreateDirectory(directory);

        var pixels = new byte[checked(clip.Width * clip.Height * 4)];
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        var yBytes = clip.Width * clip.Height;

        for (var index = 0; index < clip.FrameCount; index++)
        {
            var frame = clip.GetFrame(index);

            Nv12Converter.Convert(frame[..yBytes], clip.Width, frame[yBytes..], clip.Width,
                clip.Width, clip.Height, pixels, clip.Width * 4);

            hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));

            if (index == 0 || index == clip.FrameCount / 2 || index == clip.FrameCount - 1)
            {
                WriteBitmap(Path.Combine(directory, $"media-frame-{index:D3}.bmp"), clip.Width, clip.Height, pixels);
            }
        }

        using (var stream = new FileStream(Path.Combine(directory, "media-output.wav"), FileMode.CreateNew))
        {
            stream.Write(wave);
        }

        var peak = 0;

        foreach (var sample in clip.Pcm)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        return new MediaInspection(clip.FrameCount, hashes.Count, clip.Pcm.Length, peak,
            clip.SampleRate, clip.Channels, directory);
    }

    private static void WriteBitmap(string path, int width, int height, byte[] pixels)
    {
        using var stream = new FileStream(path, FileMode.CreateNew);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0x4D42);

        writer.Write(checked(54 + pixels.Length));

        writer.Write(0);

        writer.Write(54);

        writer.Write(40);

        writer.Write(width);

        writer.Write(-height);

        writer.Write((ushort)1);

        writer.Write((ushort)32);

        writer.Write(0);

        writer.Write(pixels.Length);

        writer.Write(0);

        writer.Write(0);

        writer.Write(0);

        writer.Write(0);

        writer.Write(pixels);
    }
}
