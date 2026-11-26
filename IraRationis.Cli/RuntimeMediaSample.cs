// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics;
using IraRationis.HLE;
using IraRationis.Media;

namespace IraRationis.Cli;

/// <summary>Explicit opt-in host service for the original IRX native media sample ABI.
/// Not a console movie ABI, and not an automatic intro or title replacement.</summary>
internal sealed unsafe class RuntimeMediaSample : IDisposable
{
    private static RuntimeMediaSample? _active;
    private readonly string _clipPath;
    private int _invoked;

    private RuntimeMediaSample(string clipPath)
    {
        _clipPath = clipPath;
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
        if (Interlocked.CompareExchange(ref _active, sample, null) is not null)
        {
            throw new InvalidOperationException("An IRX media sample session is already active.");
        }

        return sample;
    }

    internal HleModule CreateModule()
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

        try
        {
            return sample.Run();
        }
        catch (Exception exception)
        {
            // No managed exception may unwind through the native sample ABI.
            try
            {
                Console.Error.WriteLine($"[media] IRX-R44: sample failed: {exception.GetType().Name}: {exception.Message}");
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
        var clip = MediaFoundationDecoder.Decode(_clipPath);
        using var context = new ConsoleGraphicsDevice.MediaGpuContext(Console.Out);

        // Two independent playback lifetimes prove that device and PSO reuse are real.
        // Each window, swapchain, frame ring and fence is disposed before the next begins.
        for (var pass = 1; pass <= 2; pass++)
        {
            var result = GpuMediaPlaybackSession.Play(clip, (window, width, height) =>
            {
                return new Presenter(window, width, height, context);
            });
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

    public void Dispose()
    {
        _ = Interlocked.CompareExchange(ref _active, null, this);
    }

    private sealed class Presenter : IGpuVideoPresenter
    {
        private readonly ConsoleGraphicsDevice.Nv12Presenter _presenter;

        internal Presenter(nint window, int width, int height, ConsoleGraphicsDevice.MediaGpuContext context)
        {
            _presenter = new ConsoleGraphicsDevice.Nv12Presenter(window, width, height, Console.Out, context);
        }

        public bool Present(ReadOnlySpan<byte> nv12)
        {
            return _presenter.Present(nv12);
        }

        public void Dispose()
        {
            _presenter.Dispose();
        }
    }
}
