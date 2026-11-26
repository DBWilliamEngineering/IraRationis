using System.IO.Compression;
using IraRationis.HLE;

static void Require(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }

    Console.WriteLine($"PASS: {name}");
}

static byte[] Compress(byte[] data)
{
    using var memory = new MemoryStream();

    using (var stream = new ZLibStream(memory, CompressionLevel.Optimal, true))
    {
        stream.Write(data);
    }

    return memory.ToArray();
}

Require(DirectStoragePayload.IsRawLengthValid(8, 8, true), "raw exact length");
Require(!DirectStoragePayload.IsRawLengthValid(8, 16, true), "raw short rejected");
Require(!DirectStoragePayload.IsRawLengthValid(16, 8, false), "raw overflow rejected");
Require(DirectStoragePayload.IsRawLengthValid(8, 16, false), "legacy short raw accepted");
var expected = Enumerable.Range(0, 4096).Select(value => (byte)(value & 255)).ToArray();
var compressed = Compress(expected);
var exact = new byte[expected.Length];
Require(DirectStoragePayload.TryInflate(compressed, exact, true, out var exactWritten)
        && exactWritten == expected.Length && exact.AsSpan().SequenceEqual(expected), "exact zlib output");
var longer = new byte[expected.Length + 64];
Require(!DirectStoragePayload.TryInflate(compressed, longer, true, out _), "short zlib rejected");
Require(DirectStoragePayload.TryInflate(compressed, longer, false, out var shortWritten)
        && shortWritten == expected.Length, "legacy short zlib accepted");
Require(!DirectStoragePayload.TryInflate(compressed, new byte[expected.Length - 1], true, out _),
    "zlib overflow rejected");
Require(!DirectStoragePayload.TryInflate([255, 255, 255, 255], exact, true, out _), "invalid zlib rejected");
Console.WriteLine("Storage contract checks passed.");
