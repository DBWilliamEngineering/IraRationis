// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Memory.Models;

namespace IraRationis.HLE.Memory;

/// <summary>
///     Indexes disjoint, half-open mapped views. The owner serializes all access.
///     Values remain owned by the caller; removing a view does not dispose its value.
/// </summary>
internal sealed class MappedViewIndex<T> where T : class
{
    private readonly SortedSet<MappedViewEntry<T>> _entries = [with(MappedViewEntryComparer<T>.Instance)];

    public void Add(ulong address, ulong size, T value)
    {
        // O(log n), where n is the number of mapped views.
        ArgumentNullException.ThrowIfNull(value);

        if (size == 0 || size > ulong.MaxValue - address)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        var previous = FindAtOrBefore(address);

        if (previous.Size != 0 && address - previous.Address < previous.Size)
        {
            throw new ArgumentException("The mapped view overlaps an existing view.", nameof(address));
        }

        var next = _entries.GetViewBetween(new MappedViewEntry<T>(address, 0, null),
            new MappedViewEntry<T>(ulong.MaxValue, 0, null)).Min;

        if (next.Size != 0 && next.Address - address < size)
        {
            throw new ArgumentException("The mapped view overlaps an existing view.", nameof(size));
        }

        if (!_entries.Add(new MappedViewEntry<T>(address, size, value)))
        {
            throw new ArgumentException("A mapped view already starts at this address.", nameof(address));
        }
    }

    public bool Remove(ulong address)
    {
        return _entries.Remove(new MappedViewEntry<T>(address, 0, null));
    }

    public bool TryGetContaining(ulong address, out KeyValuePair<ulong, T> view)
    {
        var previous = FindAtOrBefore(address);

        if (previous.Size != 0 && address - previous.Address < previous.Size)
        {
            view = new KeyValuePair<ulong, T>(previous.Address, previous.Value!);

            return true;
        }

        view = default;

        return false;
    }

    public KeyValuePair<ulong, T>[] SnapshotOverlaps(ulong address, ulong size)
    {
        // O(log n + k) lookup and O(k) snapshot storage for k overlapping views.
        if (size > ulong.MaxValue - address)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (size == 0 || _entries.Count == 0)
        {
            return [];
        }

        List<KeyValuePair<ulong, T>>? overlaps = null;
        var previous = FindAtOrBefore(address);

        if (previous.Size != 0 && previous.Address < address && address - previous.Address < previous.Size)
        {
            overlaps = [new KeyValuePair<ulong, T>(previous.Address, previous.Value!)];
        }

        // GetViewBetween includes both bounds. Exclude the query's end address.
        // Do not read the subset's Count: it walks the subset to compute it.
        var candidates = _entries.GetViewBetween(new MappedViewEntry<T>(address, 0, null),
            new MappedViewEntry<T>(address + size - 1, 0, null));

        foreach (var candidate in candidates)
        {
            overlaps ??= [];

            overlaps.Add(new KeyValuePair<ulong, T>(candidate.Address, candidate.Value!));
        }

        if (overlaps is null)
        {
            return [];
        }

        // Capture references before the owner unmaps, removes or splits views.
        return [.. overlaps];
    }

    private MappedViewEntry<T> FindAtOrBefore(ulong address)
    {
        // O(log n); a predecessor search avoids scanning unrelated views.
        // Size zero marks an empty result or a search bound, never a stored view.
        return _entries.GetViewBetween(default, new MappedViewEntry<T>(address, 0, null)).Max;
    }
}
