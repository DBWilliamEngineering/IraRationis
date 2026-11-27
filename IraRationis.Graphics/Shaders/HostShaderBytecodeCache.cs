// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Shaders.Models;

namespace IraRationis.Graphics.Shaders;

/// <summary>
///     Bounded process-local cache for immutable host compiler bytecode. Entries are
///     keyed by exact source and profile. No guest resource or GPU object is retained.
/// </summary>
internal sealed class HostShaderBytecodeCache
{
    private readonly LinkedList<HostShaderBytecodeCacheEntry> _completed = new();
    private readonly Dictionary<(string Source, string Profile), HostShaderBytecodeCacheEntry> _entries = new();
    private readonly Lock _gate = new();
    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private long _bytes;

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
        HostShaderBytecodeCacheEntry? entry = null;

        reused = false;

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out entry))
            {
                reused = true;

                if (entry.Result is { } cached)
                {
                    Touch(entry);

                    return cached;
                }
            }
            else if (keyBytes <= _maximumBytes)
            {
                while ((_entries.Count >= _maximumEntries || keyBytes > _maximumBytes - _bytes) &&
                       EvictOldest())
                {
                }

                if (_entries.Count < _maximumEntries && keyBytes <= _maximumBytes - _bytes)
                {
                    entry = new HostShaderBytecodeCacheEntry(key, compile, keyBytes);

                    _entries.Add(key, entry);

                    _bytes += keyBytes;
                }
            }
        }

        // Pending entries are not eviction candidates. Compilation and waiting
        // remain outside the cache lock, including the bounded bypass path.
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
                RemoveIfCurrent(entry);
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
                RemoveIfCurrent(entry);

                return null;
            }

            if (entry.Result is not null)
            {
                Touch(entry);

                return result;
            }

            // An artifact that cannot fit even in an empty cache must not
            // displace unrelated, usable completed entries.
            if (result.Length > _maximumBytes - entry.Charge)
            {
                RemoveIfCurrent(entry);

                return result;
            }

            while (result.Length > _maximumBytes - _bytes && EvictOldest())
            {
            }

            if (result.Length > _maximumBytes - _bytes)
            {
                RemoveIfCurrent(entry);
            }
            else
            {
                entry.Node = _completed.AddLast(entry);
                entry.Charge += result.Length;
                _bytes += result.Length;
                entry.Result = result;
            }
        }

        return result;
    }

    // Only accounted completed entries have a node. The list is ordered from
    // least to most recently used. All list and dictionary access is locked.
    private void Touch(HostShaderBytecodeCacheEntry entry)
    {
        var node = entry.Node;

        if (node is null || ReferenceEquals(node, _completed.Last))
        {
            return;
        }

        _completed.Remove(node);

        _completed.AddLast(node);
    }

    private bool EvictOldest()
    {
        if (_completed.First is not { } oldest)
        {
            return false;
        }

        RemoveIfCurrent(oldest.Value);

        return true;
    }

    private void RemoveIfCurrent(HostShaderBytecodeCacheEntry entry)
    {
        if (!_entries.TryGetValue(entry.Key, out var current) || !ReferenceEquals(current, entry))
        {
            return;
        }

        _entries.Remove(entry.Key);

        if (entry.Node is { } node)
        {
            _completed.Remove(node);

            entry.Node = null;
        }

        _bytes -= entry.Charge;
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
        _ = cache.GetOrCompile("source", "cs", () => throw new InvalidOperationException("Unexpected miss."),
            out var hit);
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

        var bounded = new HostShaderBytecodeCache(4, 256);

        _ = bounded.GetOrCompile("keep", "cs", () => [11], out _);
        _ = bounded.GetOrCompile("oversized", "cs", () => new byte[512], out _);

        var retained = bounded.GetOrCompile("keep", "cs",
            () => throw new InvalidOperationException("R70 oversized artifact displaced a valid entry."),
            out var retainedHit);

        if (!retainedHit || retained is not [11])
        {
            throw new InvalidOperationException("R70 cache retention mismatch.");
        }

        Console.WriteLine(
            "[graphics-probe] IRX-R63: requests=16; compilations=1; profileIsolation=verified; LRU=verified; retry=verified; byteBudget=verified (CPU cache contract).");
    }
}
