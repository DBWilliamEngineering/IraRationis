// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    // CPU bytecode only: no device pointers, root signatures, PSOs or guest shader
    // containers. The compiler profile and options are fixed by this host path.
    private const int R47MaximumShaderEntries = 32;
    private const int R47MaximumShaderBytes = 8 * 1024 * 1024;
    private const int R47MaximumSourceCharacters = 64 * 1024;
    private static readonly Lock R47ShaderGate = new();
    private static readonly Dictionary<string, R47ShaderEntry> R47Shaders = new(StringComparer.Ordinal);
    private static readonly bool R47CacheEnabled =
        Environment.GetEnvironmentVariable("IRX_HOST_MEDIA_SHADER_CACHE") != "0";
    private static long _r47ShaderUse;
    private static int _r47ShaderBytes;

    private static byte[] CompileR47HostMediaShader(string source, TextWriter report)
    {
        if (!R47CacheEnabled || source.Length > R47MaximumSourceCharacters)
        {
            return CompileR47Uncached(source, report);
        }

        var entry = (R47ShaderEntry?)null;
        var hit = false;
        lock (R47ShaderGate)
        {
            if (R47Shaders.TryGetValue(source, out entry))
            {
                hit = true;
                entry.LastUse = ++_r47ShaderUse;
            }
            else
            {
                // Never evict an in-flight compilation. If every entry is pending,
                // fall back to independent compilation rather than wait under the lock.
                while (R47Shaders.Count >= R47MaximumShaderEntries && EvictR47Shader(null))
                {
                }

                if (R47Shaders.Count < R47MaximumShaderEntries)
                {
                    entry = new R47ShaderEntry(source, report, ++_r47ShaderUse);
                    R47Shaders.Add(source, entry);
                }
            }
        }

        if (entry is null)
        {
            return CompileR47Uncached(source, report);
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
            lock (R47ShaderGate)
            {
                if (R47Shaders.TryGetValue(source, out var current) && ReferenceEquals(current, entry))
                {
                    R47Shaders.Remove(source);
                }
            }

            throw;
        }

        lock (R47ShaderGate)
        {
            if (R47Shaders.TryGetValue(source, out var current) && ReferenceEquals(current, entry))
            {
                entry.LastUse = ++_r47ShaderUse;
                if (!entry.Accounted)
                {
                    // Account exactly once even if multiple waiters observed the result.
                    while (bytes.Length <= R47MaximumShaderBytes &&
                           _r47ShaderBytes > R47MaximumShaderBytes - bytes.Length && EvictR47Shader(entry))
                    {
                    }

                    if (bytes.Length > R47MaximumShaderBytes ||
                        _r47ShaderBytes > R47MaximumShaderBytes - bytes.Length)
                    {
                        // An oversized artifact can still be used by its caller, but
                        // is not retained in the cache.
                        R47Shaders.Remove(source);
                    }
                    else
                    {
                        entry.Accounted = true;
                        entry.Size = bytes.Length;
                        _r47ShaderBytes += entry.Size;
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

    // Called with R47ShaderGate held. Only already-accounted entries are eligible.
    private static bool EvictR47Shader(R47ShaderEntry? excluded)
    {
        var oldestKey = (string?)null;
        var oldestUse = long.MaxValue;
        foreach (var pair in R47Shaders)
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

        var entry = R47Shaders[oldestKey];
        R47Shaders.Remove(oldestKey);
        _r47ShaderBytes -= entry.Size;

        return true;
    }

    private static byte[] CompileR47Uncached(string source, TextWriter report)
    {
        return CompileR10ShaderCore(source, "cs_5_1", report)
               ?? throw new InvalidOperationException("R42 conversion shader compilation failed.");
    }

    private sealed class R47ShaderEntry(string source, TextWriter report, long lastUse)
    {
        public readonly Lazy<byte[]> Bytecode = new(() => CompileR47Uncached(source, report),
            LazyThreadSafetyMode.ExecutionAndPublication);
        public bool Accounted;
        public long LastUse = lastUse;
        public int Size;
    }
}
