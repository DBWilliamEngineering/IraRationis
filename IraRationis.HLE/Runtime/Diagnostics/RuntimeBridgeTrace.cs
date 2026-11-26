// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal sealed partial class RuntimeBridgeTrace
{
    private const int MaximumWriterFailures = 3;
    private const int MaximumDetailCharacters = 2048;
    private static readonly int OperationCount = Enum.GetValues<RuntimeBridgeOperation>().Length;

    private readonly TextWriter _writer;
    private readonly int _perOperationLimit;
    private readonly object _writerGate = new();
    private readonly long[] _startedCounts = new long[OperationCount];
    private readonly long[] _completedCounts = new long[OperationCount];
    private long _noteCount;
    private int _writerFailures;
    private int _summaryWritten;

    internal RuntimeBridgeTrace(TextWriter writer, int perOperationLimit = 128)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(perOperationLimit);

        _writer = writer;
        _perOperationLimit = perOperationLimit;
    }

    internal long Begin(RuntimeBridgeOperation operation, string details)
    {
        return Begin(operation, details, null);
    }

    internal long Begin(RuntimeBridgeOperation operation, string details, Func<string>? captureCaller)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            var operationIndex = (int)operation;

            if ((uint)operationIndex >= (uint)OperationCount)
            {
                return 0;
            }

            var sequence = Interlocked.Increment(ref _startedCounts[operationIndex]);
            var callId = CreateCallId(operationIndex, sequence);

            if (sequence <= _perOperationLimit)
            {
                WriteRecord($"phase=begin operation={operation} id={callId}",
                    AppendCaller(details, captureCaller));
            }
            else if (sequence == (long)_perOperationLimit + 1)
            {
                WriteRecord($"phase=limit operation={operation} id={callId}",
                    $"limit={_perOperationLimit} subsequentBeginAndEndRecordsSuppressed=true totalsContinue=true");
            }

            return callId;
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal void End(RuntimeBridgeOperation operation, long callId, string details)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            var operationIndex = (int)operation;

            if ((uint)operationIndex >= (uint)OperationCount || callId <= 0
                || (callId - 1) % OperationCount != operationIndex)
            {
                return;
            }

            Interlocked.Increment(ref _completedCounts[operationIndex]);

            if (GetOperationSequence(callId) <= _perOperationLimit)
            {
                WriteRecord($"phase=end operation={operation} id={callId}", details);
            }
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal void Note(string details)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            var sequence = Interlocked.Increment(ref _noteCount);

            if (sequence <= _perOperationLimit)
            {
                WriteRecord("phase=note", details);
            }
            else if (sequence == (long)_perOperationLimit + 1)
            {
                WriteRecord("phase=limit operation=Note",
                    $"limit={_perOperationLimit} subsequentRecordsSuppressed=true totalsContinue=true");
            }
        }
        finally
        {
            errorState.Restore();
        }
    }

    internal void WriteSummary()
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            if (Interlocked.Exchange(ref _summaryWritten, 1) != 0)
            {
                return;
            }

            for (var operationIndex = 0; operationIndex < OperationCount; operationIndex++)
            {
                var started = Interlocked.Read(ref _startedCounts[operationIndex]);
                var completed = Interlocked.Read(ref _completedCounts[operationIndex]);

                if ((RuntimeBridgeOperation)operationIndex == RuntimeBridgeOperation.QueryApi)
                {
                    WriteQuerySummary(started, completed);

                    continue;
                }

                var suppressed = Math.Max(0, started - _perOperationLimit);

                WriteRecord($"phase=summary operation={(RuntimeBridgeOperation)operationIndex}",
                    $"started={started} completed={completed} callsBeyondLimit={suppressed} limit={_perOperationLimit}");
            }

            var notes = Interlocked.Read(ref _noteCount);

            WriteRecord("phase=summary operation=Trace",
                $"notes={notes} notesBeyondLimit={Math.Max(0, notes - _perOperationLimit)} "
                + $"writerFailures={Volatile.Read(ref _writerFailures)} writerFailureLimit={MaximumWriterFailures}");
        }
        finally
        {
            errorState.Restore();
        }
    }

    private static long CreateCallId(int operationIndex, long sequence)
    {
        return (sequence - 1) * OperationCount + operationIndex + 1;
    }

    private static string AppendCaller(string details, Func<string>? captureCaller)
    {
        if (captureCaller is null)
        {
            return details;
        }

        string caller;

        try
        {
            caller = captureCaller();
        }
        catch
        {
            caller = "unavailable";
        }

        return $"{details}; caller={caller}";
    }

    private static long GetOperationSequence(long callId)
    {
        return (callId - 1) / OperationCount + 1;
    }

    private void WriteRecord(string record, string details)
    {
        lock (_writerGate)
        {
            if (_writerFailures >= MaximumWriterFailures)
            {
                return;
            }

            try
            {
                var nativeThreadId = OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;
                var detailText = LimitDetails(details);

                _writer.WriteLine($"[runtime-bridge] utc={DateTime.UtcNow:O} ticks={Stopwatch.GetTimestamp()} "
                    + $"frequency={Stopwatch.Frequency} pid={Environment.ProcessId} nativeTid={nativeThreadId} "
                    + $"{record} {detailText}");
            }
            catch
            {
                _writerFailures++;
            }
        }
    }

    private static string LimitDetails(string details)
    {
        var boundedDetails = details.Length <= MaximumDetailCharacters
            ? details
            : string.Concat(details.AsSpan(0, MaximumDetailCharacters), "...(truncated)");

        return boundedDetails.Replace("\r", "\\r").Replace("\n", "\\n");
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
