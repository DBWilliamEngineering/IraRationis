// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-pipeline/src/cache.rs.

using IraRationis.Core;

namespace IraRationis.Pipeline;

public sealed class ConversionCache
{
    private static long _sequence;

    private readonly string _root;

    public ConversionCache(string root)
    {
        _root = AssetPolicy.EnsureOutsideRepository(root);

        Directory.CreateDirectory(_root);
    }

    public string PathFor(CacheKey key)
    {
        return Path.Combine(_root, key.Hex[..2], key.Hex[2..]);
    }

    public string GetOrConvert(IConverter converter, ReadOnlySpan<byte> input)
    {
        var key = CacheKey.Create(converter, input);
        var path = PathFor(key);

        if (File.Exists(path))
        {
            return path;
        }

        var output = converter.Convert(input);
        var directory = Path.GetDirectoryName(path)!;

        AssetPolicy.EnsureOutsideRepository(directory);

        Directory.CreateDirectory(directory);

        var sequence = Interlocked.Increment(ref _sequence);
        var temporary = Path.Combine(directory, $".tmp-{Environment.ProcessId}-{sequence}-{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(output);

                stream.Flush(true);
            }

            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another publisher won the race. Its key identifies the same input.
            }

            return path;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
