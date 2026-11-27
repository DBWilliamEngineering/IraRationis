// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.HLE.Runtime.Diagnostics.RuntimeBridgeTrace;

internal sealed partial class RuntimeBridgeTrace
{
    internal const int MaximumQueryKeys = 64;
    internal const int MaximumAdditionalQueryFailures = 32;

    private readonly object _queryGate = new();
    private readonly Dictionary<RuntimeBridgeQueryKey, RuntimeBridgeQuerySeries> _querySeries = [];
    private long _additionalQueryFailures;
    private long _queryFailures;
    private long _queryReportedCalls;
    private long _queryReportedCompletions;
    private long _untrackedQueries;

    internal RuntimeBridgeQueryObservation BeginQuery(RuntimeBridgeQueryKey key, nint output,
        Func<string>? captureCaller = null)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            var operationIndex = (int)RuntimeBridgeOperation.QueryApi;
            var globalSequence = Interlocked.Increment(ref _startedCounts[operationIndex]);
            var series = FindQuerySeries(key);
            var sequence = series is null ? 0 : Interlocked.Increment(ref series.Started);
            var selected = IsQuerySample(sequence);
            var observation = new RuntimeBridgeQueryObservation(
                CreateCallId(operationIndex, globalSequence), key, series, sequence,
                Stopwatch.GetTimestamp(), output, selected);

            if (selected)
            {
                RecordQueryBegin(observation, false, captureCaller);
            }
            else if (series is null && Interlocked.Increment(ref _untrackedQueries) == 1)
            {
                WriteRecord("phase=limit operation=QueryApiKeys",
                    $"keyLimit={MaximumQueryKeys}; newKeySamplesSuppressed=True; failurePromotionBudget={MaximumAdditionalQueryFailures}");
            }

            return observation;
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal void EndQuery(RuntimeBridgeQueryObservation observation, int result, int error,
        Func<string>? captureCaller = null)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            if (observation.Id <= 0)
            {
                return;
            }

            Interlocked.Increment(ref _completedCounts[(int)RuntimeBridgeOperation.QueryApi]);

            if (observation.Series is { } series)
            {
                Interlocked.Increment(ref series.Completed);
            }

            if (result < 0)
            {
                Interlocked.Increment(ref _queryFailures);

                if (observation.Series is { } failedSeries)
                {
                    Interlocked.Increment(ref failedSeries.Failures);
                }
            }

            if (!observation.BeginRecorded)
            {
                if (result >= 0 || !PromoteQueryFailure())
                {
                    return;
                }

                RecordQueryBegin(observation, true, captureCaller);
            }

            var returnedObject = "notRead";

            if (result >= 0)
            {
                returnedObject = RuntimeBridgeMemoryReader.TryReadPointer(observation.Output, out var value)
                    ? $"0x{value:X}"
                    : "unreadable";
            }

            Interlocked.Increment(ref _queryReportedCompletions);

            WriteRecord($"phase=end operation=QueryApi id={observation.Id}",
                $"hresult=0x{unchecked((uint)result):X8}; succeeded={result >= 0}; win32Error={error}; returnedObject={returnedObject}");
        }
        finally
        {
            errorState.Restore();
        }
    }

    private RuntimeBridgeQuerySeries? FindQuerySeries(RuntimeBridgeQueryKey key)
    {
        // Expected O(1) lookup; the table retains at most 64 keys for the diagnostic session.
        lock (_queryGate)
        {
            if (_querySeries.TryGetValue(key, out var series))
            {
                return series;
            }

            if (_querySeries.Count >= MaximumQueryKeys)
            {
                return null;
            }

            series = new RuntimeBridgeQuerySeries();
            _querySeries.Add(key, series);

            return series;
        }
    }

    private static bool IsQuerySample(long sequence)
    {
        return sequence > 0 && (sequence <= 8 || (sequence & (sequence - 1)) == 0);
    }

    private bool PromoteQueryFailure()
    {
        var failure = Interlocked.Increment(ref _additionalQueryFailures);

        if (failure <= MaximumAdditionalQueryFailures)
        {
            return true;
        }

        if (failure == MaximumAdditionalQueryFailures + 1)
        {
            WriteRecord("phase=limit operation=QueryApiFailures",
                $"additionalFailureLimit={MaximumAdditionalQueryFailures}; additionalFailurePairsSuppressed=True; perKeySamplingContinues=True; totalsContinue=True");
        }

        return false;
    }

    private void RecordQueryBegin(RuntimeBridgeQueryObservation observation, bool deferred,
        Func<string>? captureCaller)
    {
        Interlocked.Increment(ref _queryReportedCalls);

        if (observation.Series is { } series)
        {
            Interlocked.Increment(ref series.Reported);
        }

        WriteRecord($"phase=begin operation=QueryApi id={observation.Id}",
            AppendCaller($"{DescribeQueryKey(observation.Key)}; outputLocation=0x{observation.Output:X}; "
                         + $"keyCount={observation.Sequence}; keyTracked={observation.Series is not null}; sampling=first8+power2; "
                         + $"startedTicks={observation.StartedTicks}; deferredForFailure={deferred}; "
                         + $"callerCapture={(deferred ? "afterResult" : "beforeTarget")}", captureCaller));
    }

    private void WriteQuerySummary(long started, long completed)
    {
        KeyValuePair<RuntimeBridgeQueryKey, RuntimeBridgeQuerySeries>[] series;

        lock (_queryGate)
        {
            series = _querySeries.ToArray();
        }

        var additionalFailures = Interlocked.Read(ref _additionalQueryFailures);
        var reportedCompletions = Interlocked.Read(ref _queryReportedCompletions);

        WriteRecord("phase=summary operation=QueryApi",
            $"started={started} completed={completed} reportedCalls={Interlocked.Read(ref _queryReportedCalls)} "
            + $"suppressedCompletions={Math.Max(0, completed - reportedCompletions)} failures={Interlocked.Read(ref _queryFailures)} "
            + $"trackedKeys={series.Length} keyLimit={MaximumQueryKeys} untrackedQueries={Interlocked.Read(ref _untrackedQueries)} "
            + $"additionalFailuresReported={Math.Min(additionalFailures, MaximumAdditionalQueryFailures)} "
            + $"additionalFailuresSuppressed={Math.Max(0, additionalFailures - MaximumAdditionalQueryFailures)} "
            + $"additionalFailureLimit={MaximumAdditionalQueryFailures}");

        foreach (var entry in series)
        {
            WriteRecord("phase=summary operation=QueryApiKey",
                $"{DescribeQueryKey(entry.Key)}; started={Interlocked.Read(ref entry.Value.Started)}; "
                + $"completed={Interlocked.Read(ref entry.Value.Completed)}; failures={Interlocked.Read(ref entry.Value.Failures)}; "
                + $"reportedCalls={Interlocked.Read(ref entry.Value.Reported)}");
        }
    }

    private static string DescribeQueryKey(RuntimeBridgeQueryKey key)
    {
        var provider = key.ProviderReadable ? key.Provider.ToString("D") : "unreadable";
        var requestedInterface = key.InterfaceReadable ? key.RequestedInterface.ToString("D") : "unreadable";

        return $"provider={provider}; interface={requestedInterface}";
    }
}
