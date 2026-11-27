// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using IraRationis.HLE.Interop;

namespace IraRationis.HLE.Runtime.Services;

internal sealed class RuntimeServiceTrace(TextWriter writer)
{
    private const int MaximumOperations = 64;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private int _writerFailures;

    internal void Write(string operation, string details)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            lock (_gate)
            {
                if (_writerFailures >= 3 || (!_counts.ContainsKey(operation) && _counts.Count >= MaximumOperations))
                {
                    return;
                }

                var count = _counts.GetValueOrDefault(operation) + 1;

                _counts[operation] = count;

                if (count > 8 && (count & (count - 1)) != 0)
                {
                    return;
                }

                try
                {
                    writer.WriteLine($"[runtime-services] utc={DateTime.UtcNow:O}; ticks={Stopwatch.GetTimestamp()}; "
                        + $"operation={operation}; count={count}; {details}");
                }
                catch
                {
                    _writerFailures++;
                }
            }
        }
        catch
        {
            Interlocked.Increment(ref _writerFailures);
        }
        finally
        {
            errorState.Restore();
        }
    }
}
