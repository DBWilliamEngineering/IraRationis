// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Core;

/// <summary>
/// Bounded, ordered preparation with overlapping CPU work. The creating thread owns
/// admission, consumption and disposal. Workers operate only on immutable or owned
/// managed snapshots; they must not submit GPU work or access borrowed native pointers.
/// </summary>
public sealed class DeferredPreparationQueue<TInput, TPrepared> : IDisposable
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly int _capacity;
    private readonly Func<TInput, CancellationToken, TPrepared> _prepare;
    private readonly Queue<Task<TPrepared>> _pending;
    private readonly SemaphoreSlim _workers;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    public DeferredPreparationQueue(Func<TInput, CancellationToken, TPrepared> prepare,
        int workerCount = 4, int capacity = 16)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerCount, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, workerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 256);

        _prepare = prepare;
        _capacity = capacity;
        _pending = new Queue<Task<TPrepared>>(capacity);
        _workers = new SemaphoreSlim(workerCount, workerCount);
    }

    public int PendingCount
    {
        get
        {
            CheckUsable();

            return _pending.Count;
        }
    }

    /// <summary>False means the caller must consume work before retrying admission.</summary>
    public bool TryEnqueue(TInput input)
    {
        CheckUsable();
        if (_pending.Count == _capacity)
        {
            return false;
        }

        var token = _stop.Token;
        var task = Task.Run(async () =>
        {
            await _workers.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();

                return _prepare(input, token);
            }
            finally
            {
                _ = _workers.Release();
            }
        }, token);

        _pending.Enqueue(task);

        return true;
    }

    /// <summary>Never blocks. Later results cannot overtake the oldest admitted item.</summary>
    public bool TryTakeNext(out TPrepared? result)
    {
        CheckUsable();
        if (!_pending.TryPeek(out var task) || !task.IsCompleted)
        {
            result = default;

            return false;
        }

        _ = _pending.Dequeue();
        result = task.GetAwaiter().GetResult();

        return true;
    }

    /// <summary>Waits for the oldest item, propagating its original failure.</summary>
    public TPrepared TakeNext()
    {
        CheckUsable();
        if (!_pending.TryDequeue(out var task))
        {
            throw new InvalidOperationException("No deferred preparation is pending.");
        }

        return task.GetAwaiter().GetResult();
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("Deferred preparation is owned by its creating thread.");
        }
    }

    private void CheckUsable()
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        CheckOwner();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stop.Cancel();
        }
        finally
        {
            // Join all admitted work before disposing the semaphore. Preparation must
            // cooperate with cancellation. Unconsumed managed snapshots are discarded.
            while (_pending.TryDequeue(out var task))
            {
                try
                {
                    _ = task.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    // Observe abandoned preparation failures during shutdown only.
                }
            }

            _workers.Dispose();
            _stop.Dispose();
        }
    }
}
