using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace IraRationis.Media;

public sealed record DecodedClip(int Width, int Height, byte[] Nv12, short[] Pcm, int SampleRate, int Channels)
{
    public int FrameBytes
    {
        get
        {
            return checked(Width * Height * 3 / 2);
        }
    }

    public int FrameCount
    {
        get
        {
            return Nv12.Length / FrameBytes;
        }
    }

    public ReadOnlySpan<byte> GetFrame(int index)
    {
        return (uint)index >= (uint)FrameCount
            ? throw new ArgumentOutOfRangeException(nameof(index))
            : Nv12.AsSpan(index * FrameBytes, FrameBytes);
    }
}

/// <summary>Bounded, local-file FFmpeg adapter for standalone authorized media tests.</summary>
public static class LocalMediaDecoder
{
    public static async Task<DecodedClip> DecodeAsync(string ffmpegPath, string inputPath,
        int width = 320, int height = 180, int seconds = 2)
    {
        if (width is < 2 or > 1920 || height is < 2 or > 1080 || (width & 1) != 0 ||
            (height & 1) != 0 || seconds is < 1 or > 5 || (long)width * height * 45 * seconds > 128L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Clip exceeds the decode budget.");
        }

        var executable = Path.GetFullPath(ffmpegPath);
        var input = Path.GetFullPath(inputPath);

        if (!File.Exists(executable) || !File.Exists(input))
        {
            throw new FileNotFoundException("FFmpeg executable or local clip not found.");
        }

        var directory = Directory.CreateTempSubdirectory("irx-media-");

        try
        {
            var video = Path.Combine(directory.FullName, "video.nv12");
            var audio = Path.Combine(directory.FullName, "audio.pcm");
            var duration = seconds.ToString(CultureInfo.InvariantCulture);

            await RunAsync(executable, input, [
                "-map", "0:v:0", "-an", "-t", duration, "-vf",
                $"scale={width}:{height}:out_color_matrix=bt709:out_range=tv,fps=30,format=nv12", "-f", "rawvideo",
                video
            ]);

            await RunAsync(executable, input, [
                "-map", "0:a:0", "-vn", "-t", duration,
                "-ac", "2", "-ar", "48000", "-f", "s16le", audio
            ]);

            var frameBytes = checked(width * height * 3 / 2);
            var videoSize = new FileInfo(video).Length;
            var audioSize = new FileInfo(audio).Length;

            if (videoSize <= 0 || videoSize % frameBytes != 0 || videoSize > (long)frameBytes * 30 * seconds ||
                audioSize <= 0 || audioSize % 4 != 0 || audioSize > 48000L * 4 * seconds)
            {
                throw new InvalidDataException("Decoded payload exceeds bounds or is misaligned.");
            }

            var frames = await File.ReadAllBytesAsync(video);
            var bytes = await File.ReadAllBytesAsync(audio);
            var pcm = new short[bytes.Length / 2];

            for (var i = 0; i < pcm.Length; i++)
            {
                pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
            }

            return new DecodedClip(width, height, frames, pcm, 48000, 2);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static async Task RunAsync(string executable, string input, string[] outputArgs)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-i", input
                 })
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var arg in outputArgs)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");

        var errors = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            await process.WaitForExitAsync();

            throw new TimeoutException("Media decode exceeded 30 seconds.");
        }

        var message = await errors;

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"FFmpeg exit {process.ExitCode}: {message}");
        }
    }
}
