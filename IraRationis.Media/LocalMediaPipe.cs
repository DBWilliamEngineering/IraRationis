// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace IraRationis.Media;

/// <summary>Owns an explicitly selected local decoder process and its binary output pipe.</summary>
internal static class LocalMediaPipe
{
    internal static async Task ReadAsync(string ffmpeg, string input, string[] arguments,
        Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken)
    {
        var executable = Path.GetFullPath(ffmpeg);
        var path = Path.GetFullPath(input);
        if (!File.Exists(executable) || !File.Exists(path))
        {
            throw new FileNotFoundException("FFmpeg executable or local media file not found.");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostdin", "-loglevel", "error",
                     "-protocol_whitelist", "file,pipe", "-i", path
                 })
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("pipe:1");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Decoder did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await consume(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var message = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"FFmpeg exit {process.ExitCode}: {message}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The child exited between the state check and termination.
                }
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            _ = await errors.ConfigureAwait(false);
        }
    }

    internal static async Task<int> FillAsync(Stream stream, Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < destination.Length)
        {
            var read = await stream.ReadAsync(destination[count..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return count;
    }
}
