// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Numerics;
using IraRationis.HLE.Input.Diagnostics.Models;
using IraRationis.HLE.Interop;

namespace IraRationis.HLE.Input.Diagnostics;

internal sealed class InputDiagnosticReport(bool enabled, Action<string>? writeLine = null)
{
    private readonly long[] _observations = new long[Enum.GetValues<InputDiagnosticCategory>().Length];
    private readonly Action<string> _writeLine = writeLine ?? Console.WriteLine;

    internal void Write<TState>(InputDiagnosticCategory category, TState state, Func<TState, string> describe)
    {
        if (!enabled)
        {
            return;
        }

        var errorState = NativeErrorState.Capture();

        try
        {
            // O(1) sampling, with at most 67 records per category before the signed 64-bit counter is exhausted.
            var observation = Interlocked.Increment(ref _observations[(int)category]);

            if (observation <= 0 || (observation > 8 && (observation & (observation - 1)) != 0))
            {
                return;
            }

            var suppressed = observation <= 8 ? 0 : observation - (8 + BitOperations.Log2((ulong)observation) - 3);
            var utc = DateTimeOffset.UtcNow;
            var timestamp = Stopwatch.GetTimestamp();
            var threadId = InputDiagnosticNativeMethods.GetCurrentThreadId();

            _writeLine($"[input] event={category}; utc={utc:O}; process={Environment.ProcessId}; thread={threadId}; "
                       + $"qpc={timestamp}; qpcFrequency={Stopwatch.Frequency}; count={observation}; "
                       + $"sampling=first8+power2; suppressedTotal={suppressed}; {describe(state)}");
        }
        catch
        {
            // Diagnostic failures must not cross an unmanaged input boundary or suppress a callback.
        }
        finally
        {
            errorState.Restore();
        }
    }
}
