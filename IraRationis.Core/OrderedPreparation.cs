// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Core;

/// <summary>
/// Bounded CPU-only preparation followed by ordered consumption on the calling thread.
/// Inputs must be immutable or exclusively owned; preparation must not submit GPU work.
/// Prepared results are managed snapshots, not borrowed pointers or GPU resource leases.
/// </summary>
public static class OrderedPreparation
{
    public static void Execute<TInput, TPrepared>(IReadOnlyList<TInput> inputs,
        Func<TInput, CancellationToken, TPrepared> prepare, Action<TPrepared> consume,
        int workerCount = 4, int capacity = 16, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 256);

        cancellationToken.ThrowIfCancellationRequested();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = cancellationToken
        };
        var total = inputs.Count;
        var results = new TPrepared[Math.Min(total, capacity)];
        var offset = 0;
        while (offset < total)
        {
            var count = Math.Min(capacity, total - offset);
            try
            {
                // Parallel.For joins all workers before consumption or cleanup.
                // A failed preparation window is never partially consumed.
                Parallel.For(0, count, options, index =>
                {
                    results[index] = prepare(inputs[offset + index], cancellationToken);
                });

                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    consume(results[index]);
                }
            }
            finally
            {
                Array.Clear(results, 0, count);
            }

            offset += count;
        }
    }
}
