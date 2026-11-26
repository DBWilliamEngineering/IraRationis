// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Media.Models;

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    // CPU bytecode only: no device pointers, root signatures, PSOs or guest shader
    // containers. The compiler profile and options are fixed by this host path.
    private const int MaximumHostMediaShaderEntries = 32;
    private const int MaximumHostMediaShaderBytes = 8 * 1024 * 1024;
    private const int MaximumHostMediaSourceCharacters = 64 * 1024;
    private static readonly Lock HostMediaShaderCacheGate = new();
    private static readonly Dictionary<string, HostMediaShaderEntry> HostMediaShaderEntries = new(StringComparer.Ordinal);
    private static readonly bool HostMediaShaderCacheEnabled =
        Environment.GetEnvironmentVariable("IRX_HOST_MEDIA_SHADER_CACHE") != "0";
    private static long _hostMediaShaderUse;
    private static int _hostMediaShaderBytes;

    private static byte[] CompileHostMediaShader(string source, TextWriter report)
    {
        if (!HostMediaShaderCacheEnabled || source.Length > MaximumHostMediaSourceCharacters)
        {
            return CompileUncachedHostMediaShader(source, report);
        }

        var entry = (HostMediaShaderEntry?)null;
        var hit = false;

        lock (HostMediaShaderCacheGate)
        {
            if (HostMediaShaderEntries.TryGetValue(source, out entry))
            {
                hit = true;

                entry.LastUse = ++_hostMediaShaderUse;
            }
            else
            {
                // Never evict an in-flight compilation. If every entry is pending,
                // fall back to independent compilation rather than wait under the lock.
                while (HostMediaShaderEntries.Count >= MaximumHostMediaShaderEntries && EvictHostMediaShader(null))
                {
                }

                if (HostMediaShaderEntries.Count < MaximumHostMediaShaderEntries)
                {
                    entry = new HostMediaShaderEntry(source, report, ++_hostMediaShaderUse);

                    HostMediaShaderEntries.Add(source, entry);
                }
            }
        }

        if (entry is null)
        {
            return CompileUncachedHostMediaShader(source, report);
        }

        // Lazy provides single-flight compilation for identical source. The cache
        // lock is deliberately not held during compilation, reporting or waiting.
        byte[] bytes;

        try
        {
            bytes = entry.Bytecode.Value;
        }
        catch
        {
            lock (HostMediaShaderCacheGate)
            {
                if (HostMediaShaderEntries.TryGetValue(source, out var current) && ReferenceEquals(current, entry))
                {
                    HostMediaShaderEntries.Remove(source);
                }
            }

            throw;
        }

        lock (HostMediaShaderCacheGate)
        {
            if (HostMediaShaderEntries.TryGetValue(source, out var current) && ReferenceEquals(current, entry))
            {
                entry.LastUse = ++_hostMediaShaderUse;

                if (!entry.Accounted)
                {
                    // Account exactly once even if multiple waiters observed the result.
                    while (bytes.Length <= MaximumHostMediaShaderBytes &&
                           _hostMediaShaderBytes > MaximumHostMediaShaderBytes - bytes.Length && EvictHostMediaShader(entry))
                    {
                    }

                    if (bytes.Length > MaximumHostMediaShaderBytes ||
                        _hostMediaShaderBytes > MaximumHostMediaShaderBytes - bytes.Length)
                    {
                        // An oversized artifact can still be used by its caller, but
                        // is not retained in the cache.
                        HostMediaShaderEntries.Remove(source);
                    }
                    else
                    {
                        entry.Accounted = true;

                        entry.Size = bytes.Length;

                        _hostMediaShaderBytes += entry.Size;
                    }
                }
            }
        }

        // The caller only pins this array for synchronous PSO creation. No code may
        // mutate it. Eviction removes a managed reference, not a live GPU object.
        if (hit)
        {
            report.WriteLine($"[media] IRX-R47: host shader cache reused; bytes={bytes.Length}.");
        }

        return bytes;
    }

    // Called with HostMediaShaderCacheGate held. Only already-accounted entries are eligible.
    private static bool EvictHostMediaShader(HostMediaShaderEntry? excluded)
    {
        var oldestKey = (string?)null;
        var oldestUse = long.MaxValue;

        foreach (var pair in HostMediaShaderEntries)
        {
            var candidate = pair.Value;

            if (candidate.Accounted && !ReferenceEquals(candidate, excluded) && candidate.LastUse < oldestUse)
            {
                oldestKey = pair.Key;

                oldestUse = candidate.LastUse;
            }
        }

        if (oldestKey is null)
        {
            return false;
        }

        var entry = HostMediaShaderEntries[oldestKey];

        HostMediaShaderEntries.Remove(oldestKey);

        _hostMediaShaderBytes -= entry.Size;

        return true;
    }

    internal static byte[] CompileUncachedHostMediaShader(string source, TextWriter report)
    {
        return CompileHostShaderCore(source, "cs_5_1", report)
               ?? throw new InvalidOperationException("R42 conversion shader compilation failed.");
    }
}
