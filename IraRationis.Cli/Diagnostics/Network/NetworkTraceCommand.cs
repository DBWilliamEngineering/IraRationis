// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace IraRationis.Cli.Diagnostics.Network;

internal static class NetworkTraceCommand
{
    internal static int Execute(string[] args, TextWriter report)
    {
        if (args.Length is < 2 or > 3 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture,
                out var processId) || processId <= 0)
        {
            throw new ArgumentException("Usage: irx network-trace <pid> <output.etl> [seconds:1..300]");
        }

        var seconds = 60;

        if (args.Length == 3 && (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                                 || seconds is < 1 or > 300))
        {
            throw new ArgumentException("Network trace duration must be between 1 and 300 seconds.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Network tracing requires Windows.");
        }

        var tracePath = Path.GetFullPath(args[1]);
        var metadataPath = tracePath + ".txt";

        if (!Path.GetExtension(tracePath).Equals(".etl", StringComparison.OrdinalIgnoreCase)
            || File.Exists(tracePath) || File.Exists(metadataPath))
        {
            throw new ArgumentException("Choose a new .etl output path; existing recordings are never overwritten.");
        }

        using var process = Process.GetProcessById(processId);

        // Retain the original process handle so exit detection cannot follow PID reuse.
        _ = process.Handle;

        if (process.HasExited)
        {
            throw new InvalidOperationException("The requested process has already exited.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(tracePath)!);

        using var metadata = new StreamWriter(new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read));
        using var cancelled = new ManualResetEventSlim();
        using var session = new NetworkTraceSession(tracePath);

        metadata.AutoFlush = true;
        metadata.WriteLine(
            $"processId={processId}; processName={process.ProcessName}; processStartUtc={process.StartTime.ToUniversalTime():O}");
        metadata.WriteLine($"session={session.Name}; circularFileMiB=64; requestedSeconds={seconds}");
        metadata.WriteLine(
            "scope=user-mode provider PID; DNS events=3006,3008; Winsock-NameResolution events=1000..1014; Winsock-Sockets events=1..6,9..12,15,16; packetCapture=False");
        metadata.WriteLine(
            "Limits: service/worker-process events can be excluded; pre-capture requests are absent; circular capture can overwrite old events; no HTTP/authentication result is inferred from socket success.");

        report.WriteLine($"[network] Starting session {session.Name} for {process.ProcessName} ({processId}).");
        session.EnableNetworkEvents((uint)processId);

        var started = Stopwatch.GetTimestamp();
        var duration = TimeSpan.FromSeconds(seconds);

        metadata.WriteLine($"captureStartUtc={DateTime.UtcNow:O}");
        report.WriteLine(
            $"[network] Capturing DNS and socket lifecycle metadata for up to {seconds} seconds. Reproduce the Y/account-picker action now. Ctrl+C stops this capture.");
        report.WriteLine(
            "[network] This observes connection activity, not application login success. Provider scope can exclude asynchronous worker/service completions.");

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancelled.Set();
        }

        Console.CancelKeyPress += Cancel;

        try
        {
            while (!process.HasExited && Stopwatch.GetElapsedTime(started) < duration && !cancelled.Wait(100))
            {
            }
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        var statistics = session.Stop();

        metadata.WriteLine(
            $"captureEndUtc={DateTime.UtcNow:O}; eventsLost={statistics.EventsLost}; buffersLost={statistics.BuffersLost}; buffersWritten={statistics.BuffersWritten}");
        report.WriteLine(
            $"[network] Saved {tracePath}; eventsLost={statistics.EventsLost}; buffersLost={statistics.BuffersLost}.");
        report.WriteLine($"[network] Capture scope and timestamps: {metadataPath}");

        return statistics.EventsLost == 0 && statistics.BuffersLost == 0 ? 0 : 3;
    }
}
