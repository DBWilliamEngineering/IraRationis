// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using IraRationis.Cli.Diagnostics.Models;
using IraRationis.Graphics.ConsoleGraphicsDevice;
using IraRationis.Graphics.Diagnostics.Models;

namespace IraRationis.Cli.Diagnostics;

/// <summary>
///     Opt-in, read-only startup evidence for one runtime invocation. No account,
///     entitlement, authentication, guest memory or input state is modified.
/// </summary>
internal sealed class RuntimeStartupObservation : IDisposable
{
    private static RuntimeStartupObservation? _active;
    private readonly StartupGraphicsSnapshot _baseline;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Timer _timer;
    private long _boundImages;
    private int _disposed;
    private long _missingImports;
    private int _phase;
    private int _reporting;
    private long _sampleCalls;
    private long _sampleCompleted;
    private long _sampleFailed;

    private RuntimeStartupObservation()
    {
        _baseline = ConsoleGraphicsDevice.CaptureStartupGraphics();
        _timer = new Timer(static state => ((RuntimeStartupObservation)state!).Report(), this,
            Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ = Interlocked.CompareExchange(ref _active, null, this);

        // Wait for an in-progress timer callback before the final report.
        _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Report();
    }

    internal static RuntimeStartupObservation? Begin()
    {
        if (Environment.GetEnvironmentVariable("IRX_STARTUP_OBSERVATION") != "1")
        {
            return null;
        }

        var observation = new RuntimeStartupObservation();

        if (Interlocked.CompareExchange(ref _active, observation, null) is not null)
        {
            observation._timer.Dispose();

            throw new InvalidOperationException("A startup observation is already active.");
        }

        observation.Report();
        _ = observation._timer.Change(2000, 2000);

        return observation;
    }

    internal void Mark(StartupPhase phase)
    {
        Volatile.Write(ref _phase, (int)phase);

        Report();
    }

    internal void RecordBinding(int missing)
    {
        Interlocked.Add(ref _missingImports, missing);
        Interlocked.Increment(ref _boundImages);
    }

    internal static void RecordSampleRequested()
    {
        var observation = Volatile.Read(ref _active);

        if (observation is not null)
        {
            Interlocked.Increment(ref observation._sampleCalls);
        }
    }

    internal static void RecordSampleResult(bool success)
    {
        var observation = Volatile.Read(ref _active);

        if (observation is null)
        {
            return;
        }

        if (success)
        {
            Interlocked.Increment(ref observation._sampleCompleted);
        }
        else
        {
            Interlocked.Increment(ref observation._sampleFailed);
        }
    }

    private void Report()
    {
        if (Interlocked.CompareExchange(ref _reporting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var graphics = ConsoleGraphicsDevice.CaptureStartupGraphics();
            var phase = (StartupPhase)Volatile.Read(ref _phase);
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[startup] IRX-R54: elapsedMs={elapsed:F0}; phase={phase}; boundImages={Interlocked.Read(ref _boundImages)}; missingImports={Interlocked.Read(ref _missingImports)}; draws={graphics.Draws - _baseline.Draws}; dispatches={graphics.Dispatches - _baseline.Dispatches}; presentAttempts={graphics.PresentAttempts - _baseline.PresentAttempts}; presentSuccesses={graphics.PresentSuccesses - _baseline.PresentSuccesses}; fenceWaits={graphics.FenceWaits - _baseline.FenceWaits}; guestWaits={graphics.GuestWaits - _baseline.GuestWaits}; authoredMediaRequests={Interlocked.Read(ref _sampleCalls)}; authoredMediaCompleted={Interlocked.Read(ref _sampleCompleted)}; authoredMediaFailed={Interlocked.Read(ref _sampleFailed)}; accountLifecycle=notObserved; guestMediaLifecycle=notObserved; pixelContent=unverified."));
        }
        catch (Exception)
        {
            // Diagnostic output must not change the runtime result.
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }
}
