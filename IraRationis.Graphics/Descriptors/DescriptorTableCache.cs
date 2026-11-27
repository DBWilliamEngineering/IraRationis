// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Descriptors.Models;

namespace IraRationis.Graphics.Descriptors;

/// <summary>
///     Fixed-size shader-visible table allocator for a single monotonic completion
///     timeline. A table occupies three contiguous descriptors. Cache hits never
///     rewrite descriptors; misses may overwrite only idle, completed slots.
///     All native resource references must remain alive externally until completion.
/// </summary>
internal sealed class DescriptorTableCache
{
    internal const int DescriptorsPerTable = 3;
    private readonly DescriptorBindingCatalog _catalog;

    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly DescriptorTableCacheSlot[] _slots;
    private ulong _completed;
    private ulong _lastSubmitted;

    internal DescriptorTableCache(DescriptorBindingCatalog catalog, int capacity)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 4096);

        _catalog = catalog;
        _slots = new DescriptorTableCacheSlot[capacity];

        for (var index = 0; index < capacity; index++)
        {
            _slots[index] = new DescriptorTableCacheSlot();
        }
    }

    internal int Hits
    {
        get;
        private set;
    }

    internal int Misses
    {
        get;
        private set;
    }

    internal int Materializations
    {
        get;
        private set;
    }

    internal int Backpressure
    {
        get;
        private set;
    }

    internal DescriptorTableCacheLease? TryAcquire(ComputeBufferTable table)
    {
        CheckOwner();

        _catalog.Validate(table);

        var candidate = -1;

        for (var index = 0; index < _slots.Length; index++)
        {
            var slot = _slots[index];

            if (slot.Active is not null || slot.Fence > _completed)
            {
                continue;
            }

            if (slot.Ready && slot.Table == table)
            {
                Hits++;

                return Activate(index, table, false);
            }

            if (candidate < 0 || (!_slots[index].Ready && _slots[candidate].Ready))
            {
                candidate = index;
            }
        }

        if (candidate < 0)
        {
            Backpressure++;

            return null;
        }

        Misses++;

        var replacement = _slots[candidate];

        replacement.Ready = false;
        replacement.Table = table;

        return Activate(candidate, table, true);
    }

    internal void MarkMaterialized(DescriptorTableCacheLease lease)
    {
        var slot = Resolve(lease);

        if (!lease.RequiresMaterialization || slot.Ready)
        {
            throw new InvalidOperationException("Descriptor table materialization is not pending.");
        }

        _catalog.Validate(lease.Table);

        slot.Ready = true;

        Materializations++;
    }

    internal void MarkSubmitted(DescriptorTableCacheLease lease, ulong fence)
    {
        var slot = Resolve(lease);

        if (!slot.Ready || fence == ulong.MaxValue || fence <= _completed || fence <= _lastSubmitted)
        {
            throw new InvalidOperationException("Descriptor table is not ready or submission fence is invalid.");
        }

        _catalog.Validate(lease.Table);

        slot.Fence = fence;
        slot.Active = null;

        _lastSubmitted = fence;
    }

    internal void Cancel(DescriptorTableCacheLease lease)
    {
        var slot = Resolve(lease);

        _catalog.Unpin(lease.Table);

        slot.Active = null;
    }

    internal void Collect(ulong completedFence)
    {
        CheckOwner();

        if (completedFence == ulong.MaxValue || completedFence < _completed || completedFence > _lastSubmitted)
        {
            throw new ArgumentOutOfRangeException(nameof(completedFence));
        }

        foreach (var slot in _slots)
        {
            if (slot.Fence == 0 || slot.Fence > completedFence)
            {
                continue;
            }

            _catalog.Unpin(slot.Table);

            slot.Fence = 0;
        }

        _completed = completedFence;
    }

    internal void EnsureIdle()
    {
        CheckOwner();

        foreach (var slot in _slots)
        {
            if (slot.Active is not null || slot.Fence > _completed)
            {
                throw new InvalidOperationException("Descriptor tables still have active or in-flight leases.");
            }
        }
    }

    private DescriptorTableCacheLease Activate(int index, ComputeBufferTable table, bool requiresMaterialization)
    {
        var lease = new DescriptorTableCacheLease(index, table, requiresMaterialization);

        _catalog.Pin(table);

        _slots[index].Active = lease;

        return lease;
    }

    private DescriptorTableCacheSlot Resolve(DescriptorTableCacheLease lease)
    {
        CheckOwner();

        ArgumentNullException.ThrowIfNull(lease);

        if ((uint)lease.SlotIndex >= (uint)_slots.Length || !ReferenceEquals(_slots[lease.SlotIndex].Active, lease))
        {
            throw new InvalidOperationException("Descriptor lease is stale or belongs to a different cache.");
        }

        return _slots[lease.SlotIndex];
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("Descriptor table cache is owned by its creating thread.");
        }
    }
}
