// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-pipeline/src/cache.rs.

using System.Buffers.Binary;
using System.Text;
using Blake3;

namespace IraRationis.Pipeline;

public sealed record CacheKey
{
    private CacheKey(string hex)
    {
        Hex = hex;
    }

    public string Hex { get; }

    public static CacheKey Create(IConverter converter, ReadOnlySpan<byte> input)
    {
        ArgumentNullException.ThrowIfNull(converter);

        using var hasher = Hasher.New();
        hasher.Update(Encoding.UTF8.GetBytes(converter.Id));
        hasher.Update(new byte[1]);

        Span<byte> version = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(version, converter.Version);
        hasher.Update(version);
        hasher.Update(input);

        var hash = hasher.Finalize();

        return new CacheKey(hash.ToString());
    }
}
