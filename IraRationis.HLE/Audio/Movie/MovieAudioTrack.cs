// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace IraRationis.HLE.Audio.Movie;

internal sealed class MovieAudioTrack(int id, uint sampleRate, uint channels, uint blockBytes, uint primeBytes, nint movie)
{
    private const int ReadyRefusalSampleMask = 63;
    private const int ReadySuccessSampleMask = 3;
    private const int ReadyRefusalSampleLimit = 16_384;
    private readonly long[] _readinessSamples = new long[(int)MovieAudioReadinessObservation.Count];
    private readonly Lock _queueGate = new();
    private long _readyCalls;
    private long _readySuccesses;
    private long _notReadyCalls;
    private long _unlockCalls;
    private long _rejectedUnlocks;
    private long _acceptedBytes;
    private long _lastAcceptance;
    private long _longestGap;
    private MovieAudioQueueSample _latestQueue;
    private long _latestQueueTimestamp;
    private long _queueSamples;
    private long _emptyQueueSamples;
    private long _cursorInconsistencies;
    private long _reportedReadyCalls = -1;
    private long _reportedUnlockCalls = -1;

    internal nint Movie { get; } = movie;

    internal bool RecordReady(bool ready)
    {
        Interlocked.Increment(ref _readyCalls);

        if (ready)
        {
            var succeeded = Interlocked.Increment(ref _readySuccesses);

            return succeeded <= ReadyRefusalSampleLimit && (succeeded <= 4 || (succeeded & ReadySuccessSampleMask) == 0);
        }

        var refused = Interlocked.Increment(ref _notReadyCalls);

        return refused <= ReadyRefusalSampleLimit && (refused <= 4 || (refused & ReadyRefusalSampleMask) == 0);
    }

    internal void RecordReadyObservation(nint channel, bool ready)
    {
        var observation = MovieAudioReadinessProbe.Capture(channel, blockBytes, out var sample);

        if (!ready)
        {
            Interlocked.Increment(ref _readinessSamples[(int)observation]);
        }

        if (sample.Capacity == 0)
        {
            return;
        }

        lock (_queueGate)
        {
            _latestQueue = sample;
            _latestQueueTimestamp = Stopwatch.GetTimestamp();
            _queueSamples++;

            if (sample.HasCoherentCursors)
            {
                if (sample.Queued == 0)
                {
                    _emptyQueueSamples++;
                }
            }
            else
            {
                _cursorInconsistencies++;
            }
        }
    }

    internal void RecordUnlock(bool accepted, uint bytes)
    {
        Interlocked.Increment(ref _unlockCalls);

        if (!accepted)
        {
            Interlocked.Increment(ref _rejectedUnlocks);

            return;
        }

        if (bytes == 0)
        {
            return;
        }

        Interlocked.Add(ref _acceptedBytes, bytes);

        var current = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref _lastAcceptance, current);

        if (previous == 0)
        {
            return;
        }

        var gap = current - previous;

        for (var longest = Volatile.Read(ref _longestGap); gap > longest; longest = Volatile.Read(ref _longestGap))
        {
            if (Interlocked.CompareExchange(ref _longestGap, gap, longest) == longest)
            {
                break;
            }
        }
    }

    internal string? TakeReport(long now)
    {
        var ready = Interlocked.Read(ref _readyCalls);
        var unlocks = Interlocked.Read(ref _unlockCalls);

        if (ready == _reportedReadyCalls && unlocks == _reportedUnlockCalls)
        {
            return null;
        }

        _reportedReadyCalls = ready;
        _reportedUnlockCalls = unlocks;

        var lastAcceptance = Volatile.Read(ref _lastAcceptance);
        var sinceLast = lastAcceptance == 0 ? 0 : Math.Max(0, Stopwatch.GetElapsedTime(lastAcceptance, now).TotalMilliseconds);
        var longest = Stopwatch.GetElapsedTime(0, Volatile.Read(ref _longestGap)).TotalMilliseconds;

        MovieAudioQueueSample queue;
        long queueTimestamp, queueSamples, emptyQueueSamples, cursorInconsistencies;

        lock (_queueGate)
        {
            queue = _latestQueue;
            queueTimestamp = _latestQueueTimestamp;
            queueSamples = _queueSamples;
            emptyQueueSamples = _emptyQueueSamples;
            cursorInconsistencies = _cursorInconsistencies;
        }

        var queueAge = queueTimestamp == 0 ? -1 : Math.Max(0, Stopwatch.GetElapsedTime(queueTimestamp, now).TotalMilliseconds);
        var unavailable = ReadObservationCount(MovieAudioReadinessObservation.Unavailable);
        var suspended = ReadObservationCount(MovieAudioReadinessObservation.Suspended);
        var disabled = ReadObservationCount(MovieAudioReadinessObservation.Disabled);
        var insufficientSpace = ReadObservationCount(MovieAudioReadinessObservation.QueueBelowRequiredSpace);
        var spaceAvailable = ReadObservationCount(MovieAudioReadinessObservation.SpaceAvailableAfterReturn);

        return string.Create(CultureInfo.InvariantCulture,
            $"track={id}; rate={sampleRate}; channels={channels}; blockBytes={blockBytes}; primeBytes={primeBytes}; " +
            $"readyCalls={ready}; notReadyCalls={Interlocked.Read(ref _notReadyCalls)}; unlocks={unlocks}; " +
            $"rejectedUnlocks={Interlocked.Read(ref _rejectedUnlocks)}; acceptedBytes={Interlocked.Read(ref _acceptedBytes)}; " +
            $"longestGapMs={longest:F2}; sinceLastAcceptanceMs={sinceLast:F2}; " +
            $"refusalSamplesUnavailable={unavailable}; refusalSamplesSuspended={suspended}; " +
            $"refusalSamplesDisabled={disabled}; refusalSamplesInsufficientSpace={insufficientSpace}; " +
            $"refusalSamplesSpaceAvailableAfterReturn={spaceAvailable}; " +
            $"queueSamples={queueSamples}; emptyQueueSamples={emptyQueueSamples}; cursorInconsistencies={cursorInconsistencies}; " +
            $"lastQueuedBytes={queue.Queued}; lastQueueCapacityBytes={queue.Capacity}; lastQueueAgeMs={queueAge:F2}; " +
            $"lastQueueReadOffset={queue.ReadOffset}; lastQueueWriteOffset={queue.WriteOffset}; lastQueueCursorsCoherent={queue.HasCoherentCursors}");
    }

    private long ReadObservationCount(MovieAudioReadinessObservation observation)
    {
        return Interlocked.Read(ref _readinessSamples[(int)observation]);
    }
}
