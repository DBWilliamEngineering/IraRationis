// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal static class RuntimeBridgeQueryTraceTests
{
    private const int Failure = unchecked((int)0x80004005);

    internal static void Run()
    {
        StartupPollingCannotHideAnotherInterface();
        UnsampledFailuresRetainTheirBeginAndEnd();
        KeyAndAdditionalFailureBudgetsStayBounded();
        ConcurrentQueriesRetainMatchingRecords();
        DiagnosticFailuresPreserveErrorState();

        Console.WriteLine(
            "Per-interface runtime query sampling, failure visibility, and correlation contracts passed.");
    }

    private static void StartupPollingCannotHideAnotherInterface()
    {
        using var writer = new StringWriter();
        var trace = new RuntimeBridgeTrace(writer, 2);
        var frequentInterface = CreateKey(1);
        var rareInterface = CreateKey(2);
        var callerCaptures = 0;
        Func<string> captureCaller = () =>
        {
            callerCaptures++;

            return "authored+0x1234";
        };

        for (var queryIndex = 0; queryIndex < 4096; queryIndex++)
        {
            CompleteQuery(trace, frequentInterface, 0, captureCaller);
        }

        var frequentRecordCount = Records(writer).Length;

        Require(frequentRecordCount <= 48 && callerCaptures <= 24,
            "Repeated startup polling must bound records and stack capture independently of call volume.");

        var rareCall = CompleteQuery(trace, rareInterface, Failure, captureCaller);
        trace.WriteSummary();
        var records = Records(writer);

        Require(records.Any(record => IsCall(record, "begin", rareCall.Id)
                                      && record.Contains($"interface={rareInterface.RequestedInterface:D}",
                                          StringComparison.Ordinal))
                && records.Any(record => IsCall(record, "end", rareCall.Id)
                                         && record.Contains("hresult=0x80004005", StringComparison.Ordinal)),
            "A failed request for a new interface must remain visible after another interface exhausts startup sampling.");
        Require(records.Any(record => record.Contains("phase=summary operation=QueryApi ", StringComparison.Ordinal)
                                      && record.Contains("started=4097 completed=4097", StringComparison.Ordinal)
                                      && record.Contains("failures=1 trackedKeys=2", StringComparison.Ordinal)),
            "Per-interface sampling must retain aggregate call, completion, and failure counts.");
        RequireMatchingPairs(records);
    }

    private static void UnsampledFailuresRetainTheirBeginAndEnd()
    {
        using var writer = new StringWriter();
        var trace = new RuntimeBridgeTrace(writer);
        var key = CreateKey(3);
        var captures = 0;
        Func<string> captureCaller = () =>
        {
            captures++;

            return "authored+0x5678";
        };

        for (var queryIndex = 0; queryIndex < 8; queryIndex++)
        {
            CompleteQuery(trace, key, 0, captureCaller);
        }

        var failure = trace.BeginQuery(key, 0, captureCaller);

        Require(!failure.BeginRecorded && captures == 8,
            "An unsampled query must defer expensive caller capture until an outcome requires a record.");

        trace.EndQuery(failure, Failure, 0, captureCaller);
        var records = Records(writer);

        Require(captures == 9
                && records.Any(record => IsCall(record, "begin", failure.Id)
                                         && record.Contains("deferredForFailure=True", StringComparison.Ordinal)
                                         && record.Contains("callerCapture=afterResult", StringComparison.Ordinal))
                && records.Any(record => IsCall(record, "end", failure.Id)
                                         && record.Contains("hresult=0x80004005", StringComparison.Ordinal)),
            "An otherwise hidden failure must emit a clearly deferred begin and matching outcome.");

        var recordsBeforeSuppressedSuccess = records.Length;
        CompleteQuery(trace, key, 0, captureCaller);

        Require(Records(writer).Length == recordsBeforeSuppressedSuccess && captures == 9,
            "A suppressed successful query must not format records or capture its caller.");
        RequireMatchingPairs(Records(writer));
    }

    private static void KeyAndAdditionalFailureBudgetsStayBounded()
    {
        using var writer = new StringWriter();
        var trace = new RuntimeBridgeTrace(writer);

        for (var keyIndex = 0; keyIndex < RuntimeBridgeTrace.MaximumQueryKeys; keyIndex++)
        {
            CompleteQuery(trace, CreateKey(keyIndex + 10), 0);
        }

        var overflowKey = CreateKey(1000);
        CompleteQuery(trace, overflowKey, 0);

        for (var failureIndex = 0; failureIndex < RuntimeBridgeTrace.MaximumAdditionalQueryFailures + 8; failureIndex++)
        {
            CompleteQuery(trace, overflowKey, Failure);
        }

        trace.WriteSummary();
        var records = Records(writer);
        var promotedFailures =
            records.Count(record => record.Contains("deferredForFailure=True", StringComparison.Ordinal));

        Require(promotedFailures == RuntimeBridgeTrace.MaximumAdditionalQueryFailures
                && records.Count(record =>
                    record.Contains("phase=limit operation=QueryApiKeys ", StringComparison.Ordinal)) == 1
                && records.Count(record =>
                    record.Contains("phase=limit operation=QueryApiFailures ", StringComparison.Ordinal)) == 1,
            "Untracked key failures must retain bounded visibility and disclose both exhausted diagnostic budgets once.");
        Require(records.Count(record =>
                    record.Contains("phase=summary operation=QueryApiKey ", StringComparison.Ordinal))
                == RuntimeBridgeTrace.MaximumQueryKeys
                && records.Any(record => record.Contains("phase=summary operation=QueryApi ", StringComparison.Ordinal)
                                         && record.Contains("additionalFailuresSuppressed=8",
                                             StringComparison.Ordinal)),
            "New query keys must not grow retained state beyond its capacity, while omitted failures remain counted.");
        RequireMatchingPairs(records);
    }

    private static void ConcurrentQueriesRetainMatchingRecords()
    {
        using var writer = new StringWriter();
        var trace = new RuntimeBridgeTrace(writer);

        Parallel.For(0, 512, queryIndex =>
            CompleteQuery(trace, CreateKey(queryIndex % 4 + 2000), queryIndex % 17 == 0 ? Failure : 0));

        trace.WriteSummary();
        var records = Records(writer);

        Require(records.Any(record => record.Contains("phase=summary operation=QueryApi ", StringComparison.Ordinal)
                                      && record.Contains("started=512 completed=512", StringComparison.Ordinal)
                                      && record.Contains("trackedKeys=4", StringComparison.Ordinal)),
            "Concurrent queries must retain exact aggregate counts and share a bounded key table.");
        RequireMatchingPairs(records);
    }

    private static void DiagnosticFailuresPreserveErrorState()
    {
        var originalErrors = NativeErrorState.Capture();

        try
        {
            using var writer = new DisruptiveRuntimeBridgeWriter(true);
            var trace = new RuntimeBridgeTrace(writer);
            var key = CreateKey(3000);
            Marshal.SetLastPInvokeError(111);
            Marshal.SetLastSystemError(222);

            var query = trace.BeginQuery(key, 0, static () => throw new IOException("Authored caller failure."));
            var afterBegin = NativeErrorState.Capture();
            trace.EndQuery(query, Failure, 0);
            var afterEnd = NativeErrorState.Capture();

            Require(afterBegin.PInvokeError == 111 && afterBegin.SystemError == 222
                                                   && afterEnd.PInvokeError == 111 && afterEnd.SystemError == 222,
                "Query diagnostics must contain caller/writer failures and preserve both native error states.");
        }
        finally
        {
            originalErrors.Restore();
        }
    }

    private static RuntimeBridgeQueryObservation CompleteQuery(RuntimeBridgeTrace trace, RuntimeBridgeQueryKey key,
        int result, Func<string>? captureCaller = null)
    {
        var observation = trace.BeginQuery(key, 0, captureCaller);
        trace.EndQuery(observation, result, 0, captureCaller);

        return observation;
    }

    private static RuntimeBridgeQueryKey CreateKey(int interfaceNumber)
    {
        return new RuntimeBridgeQueryKey(Guid.Empty, new Guid(interfaceNumber, 0, 0, new byte[8]));
    }

    private static string[] Records(StringWriter writer)
    {
        return writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void RequireMatchingPairs(string[] records)
    {
        var beginnings = records
            .Where(record => record.Contains("phase=begin operation=QueryApi ", StringComparison.Ordinal))
            .Select(CallId).ToArray();
        var endings = records
            .Where(record => record.Contains("phase=end operation=QueryApi ", StringComparison.Ordinal))
            .Select(CallId).ToArray();

        Require(beginnings.Length == beginnings.Distinct().Count() && endings.Length == endings.Distinct().Count()
                                                                   && beginnings.Order().SequenceEqual(endings.Order()),
            "Every emitted query outcome must match one unique begin record, including concurrent and promoted failures.");
    }

    private static bool IsCall(string record, string phase, long callId)
    {
        return record.Contains($"phase={phase} operation=QueryApi id={callId} ", StringComparison.Ordinal);
    }

    private static long CallId(string record)
    {
        var start = record.IndexOf(" id=", StringComparison.Ordinal) + 4;
        var end = record.IndexOf(' ', start);

        return long.Parse(record.AsSpan(start, end - start), CultureInfo.InvariantCulture);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
