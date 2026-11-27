// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Diagnostics;

namespace IraRationis.HLE.Audio.Movie;

internal sealed partial class MovieAudioFrameTrace
{
    private const int TotalFramesOffset = 0x08;
    private const int FrameNumberOffset = 0x10;
    private const int RateNumeratorOffset = 0x14;
    private const int RateDivisorOffset = 0x18;

    [ThreadStatic]
    private static uint _currentThreadId;

    private nint _movie;
    private long _lastAcceptedAudio;
    private uint _lastPacingThreadId;
    private long _pacingPolls;
    private long _pacingWaits;
    private long _decodeWaits;
    private long _decodeReady;
    private long _decodeWaitTicks;
    private long _maxDecodeWaitTicks;

    internal void RecordAudio(nint movie)
    {
        if (movie == 0)
        {
            return;
        }

        Volatile.Write(ref _movie, movie);
        Volatile.Write(ref _lastAcceptedAudio, Stopwatch.GetTimestamp());
    }

    internal bool Observes(nint movie)
    {
        return movie != 0 && movie == Volatile.Read(ref _movie);
    }

    internal void RecordPacingPoll(bool requestedWait)
    {
        if (_currentThreadId == 0)
        {
            _currentThreadId = GetCurrentThreadId();
        }

        if (Volatile.Read(ref _lastPacingThreadId) != _currentThreadId)
        {
            Volatile.Write(ref _lastPacingThreadId, _currentThreadId);
        }

        Interlocked.Increment(ref _pacingPolls);

        if (requestedWait)
        {
            Interlocked.Increment(ref _pacingWaits);
        }
    }

    internal void RecordDecodeWait(bool ready, long ticks)
    {
        Interlocked.Increment(ref _decodeWaits);

        if (ready)
        {
            Interlocked.Increment(ref _decodeReady);
        }

        Interlocked.Add(ref _decodeWaitTicks, ticks);

        for (var maximum = Volatile.Read(ref _maxDecodeWaitTicks); ticks > maximum;
             maximum = Volatile.Read(ref _maxDecodeWaitTicks))
        {
            if (Interlocked.CompareExchange(ref _maxDecodeWaitTicks, ticks, maximum) == maximum)
            {
                break;
            }
        }
    }

    internal string? TakeReport(long now)
    {
        var movie = Volatile.Read(ref _movie);
        var lastAccepted = Volatile.Read(ref _lastAcceptedAudio);

        if (movie == 0 || lastAccepted == 0 || now - lastAccepted > Stopwatch.Frequency)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{ReadFrame(movie)}; binkLastPacingThreadId={Volatile.Read(ref _lastPacingThreadId)}; " +
            $"binkPacingPolls={Interlocked.Read(ref _pacingPolls)}; " +
            $"binkPacingWaits={Interlocked.Read(ref _pacingWaits)}; binkDecodeWaits={Interlocked.Read(ref _decodeWaits)}; " +
            $"binkDecodeReady={Interlocked.Read(ref _decodeReady)}; " +
            $"binkDecodeWaitMs={Milliseconds(Interlocked.Read(ref _decodeWaitTicks)):F2}; " +
            $"binkMaxDecodeWaitMs={Milliseconds(Interlocked.Read(ref _maxDecodeWaitTicks)):F2}");
    }

    private static string ReadFrame(nint movie)
    {
        // The verified title reads these HBINK fields to drive its own frame progression.
        Span<byte> header = stackalloc byte[RateDivisorOffset + sizeof(uint)];

        if (!RuntimeBridgeMemoryReader.TryReadSnapshot(movie, header))
        {
            return "binkFrame=unavailable";
        }

        var total = BinaryPrimitives.ReadUInt32LittleEndian(header[TotalFramesOffset..]);
        var frame = BinaryPrimitives.ReadUInt32LittleEndian(header[FrameNumberOffset..]);
        var numerator = BinaryPrimitives.ReadUInt32LittleEndian(header[RateNumeratorOffset..]);
        var divisor = BinaryPrimitives.ReadUInt32LittleEndian(header[RateDivisorOffset..]);
        var rate = (double)numerator / divisor;

        if (total is > 0 and <= 1_000_000 && frame <= total && rate is >= 1 and <= 240)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"binkFrame={frame}; binkTotalFrames={total}; binkTargetFramesPerSecond={rate:F3}");
        }

        return "binkFrame=unavailable";
    }

    private static double Milliseconds(long ticks)
    {
        return ticks * (1000.0 / Stopwatch.Frequency);
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();
}
