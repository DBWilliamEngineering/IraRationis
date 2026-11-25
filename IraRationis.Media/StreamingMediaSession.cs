// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace IraRationis.Media;

public sealed record StreamingPlaybackResult(
    int DecodedFrames,
    int PresentedFrames,
    int SkippedFrames,
    bool Cancelled,
    double AudioSeconds);

/// <summary>Bounded local playback: streamed NV12, predecoded PCM, native audio master clock.</summary>
public static class StreamingMediaSession
{
    public const int FrameQueueCapacity = 4;
    private const int FramesPerSecond = 30;
    private const int SampleRate = 48000;
    private const int Channels = 2;

    public static StreamingPlaybackResult Play(string ffmpeg, string input, int seconds = 30)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (seconds is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        // Fixed output contract until timestamped, variable-resolution sources are supported.
        const int width = 640;
        const int height = 360;
        const int frameBytes = width * height * 3 / 2;
        using var audioDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        short[] pcm;
        try
        {
            pcm = DecodeAudioAsync(ffmpeg, input, seconds, audioDeadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (audioDeadline.IsCancellationRequested)
        {
            throw new TimeoutException("PCM decode exceeded its deadline.");
        }

        var duration = pcm.Length / (double)(SampleRate * Channels);
        var queue = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(FrameQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var producer = ProduceAsync(ffmpeg, input, seconds, width, height, frameBytes, queue.Writer, stop.Token);
        VideoFrame? pending = null;
        var decoded = 0;
        var presented = 0;
        var position = 0.0;
        try
        {
            // Prebuffer the first complete frame before starting the device clock.
            using var prebuffer = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            prebuffer.CancelAfter(TimeSpan.FromSeconds(10));
            if (!queue.Reader.WaitToReadAsync(prebuffer.Token).AsTask().GetAwaiter().GetResult() ||
                !queue.Reader.TryRead(out var first))
            {
                producer.GetAwaiter().GetResult();

                throw new InvalidDataException("Decoder produced no video frames.");
            }

            pending = first;
            using var window = new WindowsVideoWindow(width, height);
            using var audio = new WindowsPcmClock(pcm, SampleRate, Channels);
            var pixels = new byte[width * height * 4];
            var hasPixels = false;
            var timer = Stopwatch.StartNew();
            audio.Start();
            while (window.Pump())
            {
                if (timer.Elapsed.TotalSeconds > duration + 10)
                {
                    throw new TimeoutException("Streaming playback did not complete within its deadline.");
                }

                if (producer.IsFaulted || producer.IsCanceled)
                {
                    producer.GetAwaiter().GetResult();
                }

                var done = audio.Done;
                position = Math.Max(position, audio.Seconds);
                var target = done ? int.MaxValue : (int)(position * FramesPerSecond);
                VideoFrame? selected = null;
                try
                {
                    while (true)
                    {
                        if (pending == null)
                        {
                            if (!queue.Reader.TryRead(out var frame))
                            {
                                break;
                            }

                            pending = frame;
                        }

                        if (pending.Value.Index > target)
                        {
                            break;
                        }

                        if (selected is { } older)
                        {
                            ArrayPool<byte>.Shared.Return(older.Buffer);
                        }

                        selected = pending;
                        pending = null;
                        decoded++;
                    }

                    if (selected is { } current)
                    {
                        var data = current.Buffer.AsSpan(0, frameBytes);
                        Nv12Converter.Convert(data[..(width * height)], width, data[(width * height)..], width,
                            width, height, pixels, width * 4);
                        hasPixels = true;
                        if (window.Draw(pixels))
                        {
                            presented++;
                        }
                    }
                    else if (hasPixels)
                    {
                        window.Draw(pixels);
                    }
                }
                finally
                {
                    if (selected is { } released)
                    {
                        ArrayPool<byte>.Shared.Return(released.Buffer);
                    }
                }

                if (done && pending == null && queue.Reader.Completion.IsCompleted)
                {
                    producer.GetAwaiter().GetResult();
                    if (decoded == 0 || Math.Abs(decoded / (double)FramesPerSecond - duration) >
                        1.0 / FramesPerSecond + 0.000001)
                    {
                        throw new InvalidDataException(
                            "Decoded video and audio durations disagree by more than one frame.");
                    }

                    return new StreamingPlaybackResult(decoded, presented, decoded - presented, false, position);
                }

                Thread.Sleep(5);
            }

            return new StreamingPlaybackResult(decoded, presented, decoded - presented, true, position);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Video decoder exceeded its deadline.");
        }
        finally
        {
            stop.Cancel();
            try
            {
                producer.GetAwaiter().GetResult();
            }
            catch (Exception) when (stop.IsCancellationRequested)
            {
                // Preserve the playback error or cancellation result while stopping the producer.
            }

            if (pending is { } remaining)
            {
                ArrayPool<byte>.Shared.Return(remaining.Buffer);
            }

            while (queue.Reader.TryRead(out var remainingFrame))
            {
                ArrayPool<byte>.Shared.Return(remainingFrame.Buffer);
            }
        }
    }

    private static async Task<short[]> DecodeAudioAsync(string ffmpeg, string input, int seconds,
        CancellationToken cancellationToken)
    {
        var limit = checked(SampleRate * Channels * sizeof(short) * seconds);
        var bytes = new byte[limit];
        var count = 0;
        await LocalMediaPipe.ReadAsync(ffmpeg, input,
        [
            "-map", "0:a:0", "-vn", "-t", seconds.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-ar",
            "48000", "-f", "s16le"
        ], async (stream, token) =>
        {
            count = await LocalMediaPipe.FillAsync(stream, bytes, token).ConfigureAwait(false);
            if (count == limit && await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("Audio output exceeds the configured duration.");
            }
        }, cancellationToken).ConfigureAwait(false);
        if (count == 0 || count % (Channels * sizeof(short)) != 0)
        {
            throw new InvalidDataException("Missing or misaligned PCM output.");
        }

        var pcm = new short[count / sizeof(short)];
        for (var index = 0; index < pcm.Length; index++)
        {
            pcm[index] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index * sizeof(short), sizeof(short)));
        }

        return pcm;
    }

    private static async Task ProduceAsync(string ffmpeg, string input, int seconds, int width, int height,
        int frameBytes, ChannelWriter<VideoFrame> writer, CancellationToken cancellationToken)
    {
        try
        {
            await LocalMediaPipe.ReadAsync(ffmpeg, input,
                [
                    "-map", "0:v:0", "-an", "-t", seconds.ToString(CultureInfo.InvariantCulture), "-vf",
                    $"scale={width}:{height}:out_color_matrix=bt709:out_range=tv,fps=30,format=nv12", "-f", "rawvideo"
                ],
                async (stream, token) =>
                {
                    var index = 0;
                    while (true)
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
                        var transferred = false;
                        try
                        {
                            var count = await LocalMediaPipe.FillAsync(stream, buffer.AsMemory(0, frameBytes), token)
                                .ConfigureAwait(false);
                            if (count == 0)
                            {
                                break;
                            }

                            if (count != frameBytes || index >= seconds * FramesPerSecond)
                            {
                                throw new InvalidDataException(
                                    "Truncated frame or video duration exceeds the configured limit.");
                            }

                            await writer.WriteAsync(new VideoFrame(index++, buffer), token).ConfigureAwait(false);
                            transferred = true;
                        }
                        finally
                        {
                            if (!transferred)
                            {
                                ArrayPool<byte>.Shared.Return(buffer);
                            }
                        }
                    }
                }, cancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);

            throw;
        }
    }

    private readonly record struct VideoFrame(int Index, byte[] Buffer);
}
