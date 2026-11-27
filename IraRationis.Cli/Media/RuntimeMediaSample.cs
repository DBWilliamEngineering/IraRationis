// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Cli.Diagnostics;
using IraRationis.Graphics.ConsoleGraphicsDevice;
using IraRationis.Graphics.Media;
using IraRationis.HLE.Runtime.Models;
using IraRationis.Media.Decoding;
using IraRationis.Media.Playback;

namespace IraRationis.Cli.Media;

/// <summary>
///     Explicit opt-in host service for the original IRX native media sample ABI.
///     Not a console movie ABI, and not an automatic intro or title replacement.
/// </summary>
internal sealed unsafe class RuntimeMediaSample : IDisposable
{
    private static RuntimeMediaSample? _active;
    private readonly string _clipPath;
    private int _invoked;

    private RuntimeMediaSample(string clipPath)
    {
        _clipPath = clipPath;
    }

    public void Dispose()
    {
        _ = Interlocked.CompareExchange(ref _active, null, this);
    }

    internal static RuntimeMediaSample? Begin()
    {
        var path = Environment.GetEnvironmentVariable("IRX_SAMPLE_MEDIA_CLIP");

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Path.GetFullPath(path);

        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("IRX_SAMPLE_MEDIA_CLIP must select an existing local MP4.");
        }

        var sample = new RuntimeMediaSample(path);

        return Interlocked.CompareExchange(ref _active, sample, null) is not null
            ? throw new InvalidOperationException("An IRX media sample session is already active.")
            : sample;
    }

    internal static HleModule CreateModule()
    {
        var address = (nuint)(delegate* unmanaged[Stdcall]<int>)&Play;

        return new HleModule("IRX authored media sample", ["irx-sample-media.dll"],
            [new HleExport("IrxPlaySampleMedia", address)]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Play()
    {
        var sample = Volatile.Read(ref _active);

        if (sample is null || Thread.CurrentThread.Name != "title-main" ||
            Interlocked.CompareExchange(ref sample._invoked, 1, 0) != 0)
        {
            return 1;
        }

        RuntimeStartupObservation.RecordSampleRequested();

        try
        {
            var result = sample.Run();

            RuntimeStartupObservation.RecordSampleResult(result == 0);

            return result;
        }
        catch (Exception exception)
        {
            RuntimeStartupObservation.RecordSampleResult(false);

            // No managed exception may unwind through the native sample ABI.
            try
            {
                Console.Error.WriteLine(
                    $"[media] IRX-R44: sample failed: {exception.GetType().Name}: {exception.Message}");
            }
            catch (Exception)
            {
                // Reporting is best-effort at the unmanaged boundary.
            }

            return 2;
        }
    }

    private int Run()
    {
        var decoder = Environment.GetEnvironmentVariable("IRX_SAMPLE_MEDIA_FFMPEG");

        if (!string.IsNullOrWhiteSpace(decoder))
        {
            return RunStreaming(decoder);
        }

        var clip = MediaFoundationDecoder.Decode(_clipPath);

        using var context = new MediaGpuContext(Console.Out, true);

        // Two independent playback lifetimes prove that device and PSO reuse are real.
        // Each window, SwapChain, frame ring and fence is disposed before the next begins.
        for (var pass = 1; pass <= 2; pass++)
        {
            var result = GpuMediaPlaybackSession.Play(clip,
                (window, width, height) => new RuntimeMediaPresenter(window, width, height, context));

            Console.WriteLine($"[media] IRX-R44: pass={pass}; presented={result.PresentedFrames}; " +
                              $"skipped={result.SkippedFrames}; cancelled={result.Cancelled}; " +
                              $"audioSeconds={result.AudioSeconds:F3}; sourceFrames={clip.FrameCount}.");

            if (result.Cancelled || result.PresentedFrames != clip.FrameCount || result.SkippedFrames != 0)
            {
                return 3;
            }
        }

        return 0;
    }

    private int RunStreaming(string decoder)
    {
        using var context = new MediaGpuContext(Console.Out, true);

        if (Environment.GetEnvironmentVariable("IRX_SAMPLE_SCALAR_BATCH") == "1")
        {
            ConsoleGraphicsDevice.VerifyAuthoredScalarBatch(context);
        }

        if (Environment.GetEnvironmentVariable("IRX_SAMPLE_PROGRAMMABLE_RASTER") == "1")
        {
            ConsoleGraphicsDevice.VerifyAuthoredRaster(context);
        }

        var result = StreamingMediaSession.PlayGpu(decoder, _clipPath,
            (window, width, height) => new RuntimeMediaPresenter(window, width, height, context));

        Console.WriteLine(FormattableString.Invariant(
            $"[media] IRX-R56: decoded={result.DecodedFrames}; presented={result.PresentedFrames}; skipped={result.SkippedFrames}; cancelled={result.Cancelled}; audioSeconds={result.AudioSeconds:F3}; queueCapacity={StreamingMediaSession.FrameQueueCapacity}."));

        return result.Cancelled || result.PresentedFrames == 0 || result.SkippedFrames != 0 ? 3 : 0;
    }
}
