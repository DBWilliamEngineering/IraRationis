// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using IraRationis.Media.Audio.Models;

namespace IraRationis.Media.Audio;

/// <summary>
///     Diagnostic sink for one decoded voice. Copies PCM into a bounded queue and owns
///     one native output device on a worker thread. It is not a final application mixer.
/// </summary>
public sealed unsafe partial class WindowsPcmMonitor : IPcmPacketSink, IDisposable
{
    private const int MaxFrames = 5760;
    private const int QueueCapacity = 8;
    private const int NativeSlots = 4;
    private const int HeaderDone = 1;
    private readonly Channel<short[]> _queue = Channel.CreateBounded<short[]>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Thread _worker;
    private long _accepted;
    private int _closed;
    private long _completed;
    private long _dropped;
    private int _faulted;

    public WindowsPcmMonitor()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        _worker = new Thread(Consume)
        {
            IsBackground = true,
            Name = "irx PCM monitor"
        };

        _worker.Start();
    }

    public long CompletedPackets
    {
        get
        {
            return Interlocked.Read(ref _completed);
        }
    }

    public bool Faulted
    {
        get
        {
            return Volatile.Read(ref _faulted) != 0;
        }
    }

    /// <summary>Stop accepting packets; the worker drains accepted audio without blocking the caller.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);

        _queue.Writer.TryComplete();
    }

    public bool TrySubmit(ReadOnlySpan<short> samples, int channels)
    {
        if (channels is not (1 or 2) || samples.IsEmpty || samples.Length % channels != 0 ||
            samples.Length / channels > MaxFrames || Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        var frames = samples.Length / channels;
        var stereo = new short[frames * 2];

        if (channels == 2)
        {
            samples.CopyTo(stereo);
        }
        else
        {
            for (var index = 0; index < frames; index++)
            {
                stereo[index * 2] = samples[index];

                stereo[index * 2 + 1] = samples[index];
            }
        }

        if (!_queue.Writer.TryWrite(stereo))
        {
            Interlocked.Increment(ref _dropped);

            return false;
        }

        Interlocked.Increment(ref _accepted);

        return true;
    }

    public bool WaitForCompletion(TimeSpan timeout)
    {
        return _worker.Join(timeout);
    }

    private void Consume()
    {
        var device = (nint)0;
        var headers = (WaveHeader*)null;
        var data = (short*)null;
        var prepared = new bool[NativeSlots];
        var active = 0;
        var safeToFree = true;

        try
        {
            var format = new WaveFormat
            {
                Tag = 1,
                Channels = 2,
                SamplesPerSecond = 48000,
                AverageBytesPerSecond = 192000,
                BlockAlign = 4,
                BitsPerSample = 16
            };

            Check(WaveOutOpen(out device, uint.MaxValue, &format, 0, 0, 0), "open");

            headers = (WaveHeader*)NativeMemory.AllocZeroed(NativeSlots, (nuint)sizeof(WaveHeader));

            data = (short*)NativeMemory.Alloc(NativeSlots * MaxFrames * 2, sizeof(short));

            Console.Error.WriteLine("[audio] IRX-R40-MONITOR: Windows PCM device opened; rate=48000; channels=2.");

            var progress = Stopwatch.StartNew();

            for(;;)
            {
                for (var slot = 0; slot < NativeSlots; slot++)
                {
                    var header = headers + slot;

                    if (prepared[slot] && (Volatile.Read(ref header->Flags) & HeaderDone) != 0)
                    {
                        Check(WaveOutUnprepareHeader(device, header, (uint)sizeof(WaveHeader)), "unprepare");

                        prepared[slot] = false;

                        active--;

                        Interlocked.Increment(ref _completed);

                        progress.Restart();
                    }

                    if (prepared[slot] || !_queue.Reader.TryRead(out var samples))
                    {
                        continue;
                    }

                    var buffer = data + slot * MaxFrames * 2;

                    samples.AsSpan().CopyTo(new Span<short>(buffer, samples.Length));

                    *header = default;

                    header->Data = (nint)buffer;

                    header->Length = (uint)(samples.Length * sizeof(short));

                    Check(WaveOutPrepareHeader(device, header, (uint)sizeof(WaveHeader)), "prepare");

                    prepared[slot] = true;

                    active++;

                    Check(WaveOutWrite(device, header, (uint)sizeof(WaveHeader)), "write");

                    progress.Restart();
                }

                if (_queue.Reader.Completion.IsCompleted && active == 0)
                {
                    break;
                }

                if (active != 0 && progress.Elapsed > TimeSpan.FromSeconds(5))
                {
                    throw new TimeoutException("PCM device stopped completing buffers.");
                }

                if (active == 0)
                {
                    progress.Restart();
                }

                Thread.Sleep(2);
            }
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _faulted, 1);

            Console.Error.WriteLine($"[audio] IRX-R40-MONITOR: output failed: {exception.Message}");
        }
        finally
        {
            Dispose();

            while (_queue.Reader.TryRead(out _))
            {
                Interlocked.Increment(ref _dropped);
            }

            if (device != 0)
            {
                var reset = WaveOutReset(device);

                for (var slot = 0; slot < NativeSlots; slot++)
                {
                    if (prepared[slot])
                    {
                        var result = WaveOutUnprepareHeader(device, headers + slot, (uint)sizeof(WaveHeader));

                        if (result != 0)
                        {
                            safeToFree = false;
                        }
                    }
                }

                var close = WaveOutClose(device);

                if (!safeToFree || reset != 0 || close != 0)
                {
                    Interlocked.Exchange(ref _faulted, 1);

                    Console.Error.WriteLine(
                        $"[audio] IRX-R40-MONITOR: cleanup reset={reset}; close={close}; buffersReleased={safeToFree}.");
                }
            }

            // A driver that still owns a header must never see freed memory.
            if (safeToFree)
            {
                NativeMemory.Free(headers);

                NativeMemory.Free(data);
            }

            Console.Error.WriteLine(
                $"[audio] IRX-R40-MONITOR: accepted={Interlocked.Read(ref _accepted)}; completed={Interlocked.Read(ref _completed)}; dropped={Interlocked.Read(ref _dropped)}.");
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"waveOut {operation} failed (MMRESULT {result}).");
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutOpen(out nint device, uint id, WaveFormat* format, nuint callback,
        nuint instance, uint flags);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutPrepareHeader(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutWrite(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutUnprepareHeader(nint device, WaveHeader* header, uint size);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutReset(nint device);

    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaveOutClose(nint device);
}
