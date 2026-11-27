// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using IraRationis.HLE.Input.Diagnostics;
using IraRationis.HLE.Input.Diagnostics.Models;

namespace IraRationis.Runtime.Tests.Input;

internal static class InputDiagnosticReportTests
{
    internal static void Run()
    {
        DisabledReportsDoNotFormat();
        FrequentReadsPreserveRareEvents();
        ConcurrentReportsStayBounded();
        ReportsCarryClockAndThreadEvidence();
        DiagnosticsPreserveErrorState();

        Console.WriteLine("Bounded input diagnostic correlation contracts passed.");
    }

    private static void DisabledReportsDoNotFormat()
    {
        var formatted = false;
        var written = false;
        var report = new InputDiagnosticReport(false, _ => written = true);

        report.Write(InputDiagnosticCategory.GuestReading, 0, _ =>
        {
            formatted = true;

            return "unexpected";
        });

        Require(!formatted && !written, "Disabled input tracing must neither format nor emit a record.");
    }

    private static void FrequentReadsPreserveRareEvents()
    {
        var records = new List<string>();
        var report = new InputDiagnosticReport(true, records.Add);
        var formatted = 0;

        for (var observation = 1; observation <= 4096; observation++)
        {
            report.Write(InputDiagnosticCategory.CurrentReading, observation, value =>
            {
                formatted++;

                return $"reading={value}";
            });
        }

        Require(records.Count <= 24 && records.Count == formatted,
            "Frequent reads must have a bounded output budget, and suppressed reads must not be formatted.");
        Require(records[^1].Contains("count=4096;", StringComparison.Ordinal)
                && records[^1].Contains("suppressedTotal=4079;", StringComparison.Ordinal),
            "A later input sample must disclose how many earlier observations sampling omitted.");

        report.Write(InputDiagnosticCategory.FindDeviceFailure, 0, static _ => "result=0x838A0002");
        report.Write(InputDiagnosticCategory.GuestYPressed, 0, static _ => "Y=True");
        report.Write(InputDiagnosticCategory.GuestYReleased, 0, static _ => "Y=False");

        Require(records[^3].Contains("event=FindDeviceFailure;", StringComparison.Ordinal)
                && records[^2].Contains("event=GuestYPressed;", StringComparison.Ordinal)
                && records[^1].Contains("event=GuestYReleased;", StringComparison.Ordinal),
            "Frequent polling must not consume lookup failures or the first guest Y press and release records.");
    }

    private static void ConcurrentReportsStayBounded()
    {
        var writes = 0;
        var report = new InputDiagnosticReport(true, _ => Interlocked.Increment(ref writes));

        Parallel.For(0, 8192, observation =>
            report.Write(InputDiagnosticCategory.DeviceCallbackEnter, observation,
                static value => $"delivery={value}"));

        Require(writes is > 0 and <= 24,
            "Concurrent callback delivery must retain a shared, bounded diagnostic budget.");
    }

    private static void ReportsCarryClockAndThreadEvidence()
    {
        string? record = null;
        var report = new InputDiagnosticReport(true, value => record = value);
        var earliestUtc = DateTimeOffset.UtcNow;
        var earliestTimestamp = Stopwatch.GetTimestamp();

        report.Write(InputDiagnosticCategory.DeviceRegistration, 42UL, static token => $"token={token}");

        var latestTimestamp = Stopwatch.GetTimestamp();
        var latestUtc = DateTimeOffset.UtcNow;

        Require(record is not null, "The first enabled diagnostic record must be emitted.");

        var fields = record!.Split("; ")
            .Select(field => field.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var utc = DateTimeOffset.Parse(fields["utc"], CultureInfo.InvariantCulture);
        var timestamp = long.Parse(fields["qpc"], CultureInfo.InvariantCulture);

        Require(
            utc >= earliestUtc && utc <= latestUtc && timestamp >= earliestTimestamp && timestamp <= latestTimestamp,
            "Input diagnostics must carry current UTC and monotonic timestamps for cross-log correlation.");
        Require(long.Parse(fields["qpcFrequency"], CultureInfo.InvariantCulture) == Stopwatch.Frequency
                && int.Parse(fields["process"], CultureInfo.InvariantCulture) == Environment.ProcessId
                && uint.Parse(fields["thread"], CultureInfo.InvariantCulture)
                == InputDiagnosticNativeMethods.GetCurrentThreadId(),
            "Input diagnostic timestamps must identify their frequency, process, and native thread.");
    }

    private static void DiagnosticsPreserveErrorState()
    {
        var originalSystemError = Marshal.GetLastSystemError();
        var originalInvocationError = Marshal.GetLastPInvokeError();

        try
        {
            var report = new InputDiagnosticReport(true, _ =>
            {
                Marshal.SetLastPInvokeError(333);
                Marshal.SetLastSystemError(444);

                throw new IOException("Authored diagnostic sink failure.");
            });

            Marshal.SetLastPInvokeError(111);
            Marshal.SetLastSystemError(222);

            report.Write(InputDiagnosticCategory.DeviceCallbackEnter, 1, static _ => "delivery=authored");

            var systemError = Marshal.GetLastSystemError();
            var invocationError = Marshal.GetLastPInvokeError();

            Require(systemError == 222 && invocationError == 111,
                "A failing diagnostic sink must preserve both native and managed error state without escaping.");

            Marshal.SetLastPInvokeError(555);
            Marshal.SetLastSystemError(666);

            report.Write(InputDiagnosticCategory.DeviceCallbackReturn, 1,
                static _ => throw new IOException("Authored diagnostic formatting failure."));

            systemError = Marshal.GetLastSystemError();
            invocationError = Marshal.GetLastPInvokeError();

            Require(systemError == 666 && invocationError == 555,
                "A failing diagnostic formatter must preserve both native and managed error state without escaping.");
        }
        finally
        {
            Marshal.SetLastPInvokeError(originalInvocationError);
            Marshal.SetLastSystemError(originalSystemError);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
