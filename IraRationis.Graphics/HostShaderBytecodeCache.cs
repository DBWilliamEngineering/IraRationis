// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

/// <summary>
///     Bounded process-local cache for immutable host compiler bytecode. Entries are
///     keyed by exact source and profile. No guest resource or GPU object is retained.
/// </summary>
internal sealed class HostShaderBytecodeCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Source, string Profile), Entry> _entries = new();
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private long _bytes;
    private long _sequence;

    internal HostShaderBytecodeCache(int maximumEntries, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
    }

    internal byte[]? GetOrCompile(string source, string profile, Func<byte[]?> compile, out bool reused)
    {
        var key = (source, profile);
        var keyBytes = ((long)source.Length + profile.Length) * sizeof(char);
        Entry? entry = null;

        reused = false;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out entry))
            {
                reused = true;
                entry.LastUse = ++_sequence;
            }
            else if (keyBytes <= _maximumBytes)
            {
                while ((_entries.Count >= _maximumEntries || keyBytes > _maximumBytes - _bytes) &&
                       Evict(null))
                {
                }

                if (_entries.Count < _maximumEntries && keyBytes <= _maximumBytes - _bytes)
                {
                    entry = new Entry(compile, keyBytes, ++_sequence);

                    _entries.Add(key, entry);

                    _bytes += keyBytes;
                }
            }
        }

        // When every slot is pending, preserve bounded retention rather than
        // evict in-flight compiles. Independent keys may compile concurrently.
        if (entry is null)
        {
            return compile();
        }

        byte[]? result;

        try
        {
            result = entry.Value.Value;
        }
        catch
        {
            lock (_gate)
            {
                RemoveIfCurrent(key, entry);
            }

            throw;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return result;
            }

            if (result is null)
            {
                RemoveIfCurrent(key, entry);

                return null;
            }

            entry.LastUse = ++_sequence;

            if (!entry.Completed)
            {
                while (result.Length > _maximumBytes - _bytes && Evict(entry))
                {
                }

                if (result.Length > _maximumBytes - _bytes)
                {
                    RemoveIfCurrent(key, entry);
                }
                else
                {
                    entry.Charge += result.Length;
                    _bytes += result.Length;
                    entry.Completed = true;
                }
            }
        }

        return result;
    }

    // All metadata mutations occur under _gate. No caller, compiler, or waiter
    // runs under this lock. Only successfully accounted results can be evicted.
    private bool Evict(Entry? exclude)
    {
        var key = (Source: string.Empty, Profile: string.Empty);
        Entry? oldest = null;

        foreach (var candidate in _entries)
        {
            var entry = candidate.Value;

            if (entry.Completed && !ReferenceEquals(entry, exclude) &&
                (oldest is null || entry.LastUse < oldest.LastUse))
            {
                oldest = entry;
                key = candidate.Key;
            }
        }

        if (oldest is null)
        {
            return false;
        }

        RemoveIfCurrent(key, oldest);

        return true;
    }

    private void RemoveIfCurrent((string Source, string Profile) key, Entry entry)
    {
        if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(key);

            _bytes -= entry.Charge;
        }
    }

    private sealed class Entry(Func<byte[]?> compile, long charge, long lastUse)
    {
        internal readonly Lazy<byte[]?> Value = new(compile, LazyThreadSafetyMode.ExecutionAndPublication);
        internal long Charge = charge;
        internal long LastUse = lastUse;
        internal bool Completed;
    }

    internal static void Verify()
    {
        var cache = new HostShaderBytecodeCache(2, 256);
        var calls = 0;
        byte[] expected = [1, 2, 3, 4];

        Parallel.For(0, 16, index =>
        {
            var actual = cache.GetOrCompile("source", "cs", () =>
            {
                Interlocked.Increment(ref calls);

                return expected;
            }, out _);

            if (!ReferenceEquals(expected, actual))
            {
                throw new InvalidOperationException("R63 concurrent cache result mismatch.");
            }
        });

        if (calls != 1)
        {
            throw new InvalidOperationException("R63 duplicated identical compilation.");
        }

        _ = cache.GetOrCompile("source", "vs", () => [9], out var crossProfile);
        _ = cache.GetOrCompile("source", "cs", () => throw new InvalidOperationException("Unexpected miss."), out var hit);
        _ = cache.GetOrCompile("other", "cs", () => [8], out _);
        _ = cache.GetOrCompile("source", "vs", () => [9], out var evicted);

        if (crossProfile || !hit || evicted)
        {
            throw new InvalidOperationException("R63 cache key or eviction mismatch.");
        }

        var failure = cache.GetOrCompile("fail", "cs", () => null, out _);
        var retry = cache.GetOrCompile("fail", "cs", () => [7], out var failedHit);

        if (failure is not null || retry is not [7] || failedHit)
        {
            throw new InvalidOperationException("R63 cached an unsuccessful compilation.");
        }

        _ = cache.GetOrCompile("large", "cs", () => new byte[512], out _);
        _ = cache.GetOrCompile("large", "cs", () => [6], out var oversizedHit);

        if (oversizedHit)
        {
            throw new InvalidOperationException("R63 retained an oversized artifact.");
        }

        Console.WriteLine("[graphics-probe] IRX-R63: requests=16; compilations=1; profileIsolation=verified; LRU=verified; retry=verified; byteBudget=verified (CPU cache contract).");
    }
}
