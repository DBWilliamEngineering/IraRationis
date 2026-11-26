// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Core;

/// <summary>
///     Bounded FIFO retirement for one monotonic GPU completion timeline. The creating
///     thread owns this queue. Each admitted item transfers one unique resource lease;
///     callers must not retire the same lease twice or mix independent fence timelines.
/// </summary>
public sealed class FenceRetirementQueue<T> where T : class, IDisposable
{
    private readonly int _capacity;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly Queue<Entry> _pending;
    private ulong _completed;
    private ulong _lastAdmitted;

    public FenceRetirementQueue(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);

        _capacity = capacity;

        _pending = new Queue<Entry>(capacity);
    }

    public int PendingCount
    {
        get
        {
            CheckOwner();

            return _pending.Count;
        }
    }

    /// <summary>On false or exception the caller retains ownership.</summary>
    public bool TryRetire(ulong submittedFence, T resource)
    {
        CheckOwner();

        ArgumentNullException.ThrowIfNull(resource);

        if (submittedFence == 0 || submittedFence == ulong.MaxValue || submittedFence < _lastAdmitted)
        {
            throw new ArgumentOutOfRangeException(nameof(submittedFence));
        }

        if (_pending.Count == _capacity)
        {
            return false;
        }

        _pending.Enqueue(new Entry(submittedFence, resource));

        _lastAdmitted = submittedFence;

        return true;
    }

    /// <summary>
    ///     Release only leases covered by a genuine completed fence value. A removed
    ///     device sentinel must not be treated as normal completion. Lease disposal
    ///     must not throw or reenter this queue.
    /// </summary>
    public int Collect(ulong completedFence)
    {
        CheckOwner();

        if (completedFence == ulong.MaxValue || completedFence < _completed)
        {
            throw new ArgumentOutOfRangeException(nameof(completedFence));
        }

        _completed = completedFence;

        var count = 0;

        while (_pending.TryPeek(out var entry) && entry.Fence <= _completed)
        {
            _ = _pending.Dequeue();

            entry.Resource.Dispose();

            count++;
        }

        return count;
    }

    /// <summary>No forced disposal: the owner must establish GPU completion first.</summary>
    public void EnsureEmpty()
    {
        CheckOwner();

        if (_pending.Count != 0)
        {
            throw new InvalidOperationException("GPU resource retirement is incomplete.");
        }
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("Fence retirement is owned by its creating thread.");
        }
    }

    private readonly record struct Entry(ulong Fence, T Resource);
}
