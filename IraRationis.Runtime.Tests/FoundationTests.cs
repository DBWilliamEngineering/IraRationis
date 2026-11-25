// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using Blake3;
using IraRationis.Core;
using IraRationis.Game;
using IraRationis.Loader;
using IraRationis.PE;
using IraRationis.Pipeline;

namespace IraRationis.Runtime.Tests;

internal static class FoundationTests
{
    public static void Run()
    {
        TestArchive();
        TestCache();
        TestConfiguration();
        TestPreparation();
    }

    private static void TestArchive()
    {
        var payload = "first container"u8.ToArray();
        using var packed = new MemoryStream();

        using (var compressor = new DeflateStream(packed, CompressionLevel.Optimal, true))
        {
            compressor.Write(payload);
        }

        using var file = new MemoryStream();
        using var writer = new BinaryWriter(file);
        var name = "VS_Blit_0011aabb_Wrapped"u8.ToArray();
        var size = (uint)packed.Length + 12;

        writer.Write("SGD5"u8);
        writer.Write((byte)(name.Length + 1));
        writer.Write(name);
        writer.Write((byte)0);
        writer.Write((byte)0x32);
        writer.Write(size);
        writer.Write(0x000DDCE7U);
        writer.Write(size);
        writer.Write((uint)payload.Length);
        writer.Write(packed.ToArray());
        writer.Flush();

        var bytes = file.ToArray();
        var record = Sgd5.Records(bytes).Single();

        Require(Sgd5.IsSgd5(bytes), "SGD5 magic");
        Require(record.Name == "VS_Blit_0011aabb_Wrapped" && record.Kind == 0x32, "SGD5 metadata");
        Require(record.Unpack()?.SequenceEqual(payload) == true, "SGD5 payload");
        Require(!Sgd5.Records(bytes[..^1]).Any(), "SGD5 truncated record");
        Require((record with { UnpackedSize = 1 }).Unpack() is null, "SGD5 output bound");
    }

    private static void TestCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"irx-test-{Guid.NewGuid():N}");

        try
        {
            var cache = new ConversionCache(root);
            var converter = new UpperConverter(1);
            var first = cache.GetOrConvert(converter, "abc"u8);
            var second = cache.GetOrConvert(converter, "abc"u8);

            Require(first == second && converter.Runs == 1, "Cache reuse");
            Require(File.ReadAllText(first) == "ABC", "Cache payload");
            Require(CacheKey.Create(converter, "x"u8) != CacheKey.Create(new UpperConverter(2), "x"u8),
                "Cache version");
            Require(
                Hasher.Hash("BLAKE3"u8).ToString()
                == "f890484173e516bfd935ef3d22b912dc9738de38743993cfedf2c9473b3216a4", "BLAKE3 known vector");

            File.WriteAllText(Path.Combine(root, AssetPolicy.RootMarker), string.Empty);
            var rejected = false;

            try
            {
                _ = new ConversionCache(Path.Combine(root, "forbidden"));
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            Require(rejected, "Repository asset guard");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void TestConfiguration()
    {
        var paths = new ConfigPaths
            { GameDirectory = "file-game", CacheDirectory = "file-cache", TestTitles = ["file-title"] };
        var config = RuntimeConfig.Resolve(paths, null, key => key == "GAME_DIR" ? "env-game" : string.Empty);

        Require(config.GameDirectory == "env-game" && config.CacheDirectory == "file-cache", "Config precedence");
        Require(config.TestTitles.SequenceEqual(paths.TestTitles), "Config empty environment fallback");
    }

    private static void TestPreparation()
    {
        var bytes = new byte[0x400];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Write32(bytes, 0x3C, 0x80);
        Write32(bytes, 0x80, 0x4550);
        Write16(bytes, 0x84, 0x8664);
        Write16(bytes, 0x86, 1);
        Write16(bytes, 0x94, 240);
        Write16(bytes, 0x96, 0x22);
        Write16(bytes, 0x98, 0x20B);
        Write32(bytes, 0x98 + 16, 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x98 + 24), 0x140000000UL);
        Write32(bytes, 0x98 + 56, 0x2000);
        Write32(bytes, 0x98 + 60, 0x200);
        Write16(bytes, 0x98 + 68, 14);
        Write32(bytes, 0x98 + 108, 16);
        ".text"u8.CopyTo(bytes.AsSpan(0x188));
        Write32(bytes, 0x188 + 8, 0x200);
        Write32(bytes, 0x188 + 12, 0x1000);
        Write32(bytes, 0x188 + 16, 0x200);
        Write32(bytes, 0x188 + 20, 0x200);
        Write32(bytes, 0x188 + 36, 0x60000020);
        var prepared = ImagePreparation.Prepare(bytes);
        var patched = PeImage.Parse(prepared.Bytes);

        Require(prepared.Info.EntryRva == 0x1000 && patched.EntryRva == 0 && patched.IsDll, "PE preparation");
        Require(PeImage.Parse(bytes).EntryRva == 0x1000 && !PeImage.Parse(bytes).IsDll, "Original image unchanged");
    }

    private static void Write16(byte[] bytes, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    }

    private static void Write32(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Failed: {label}");
        }
    }

    private sealed class UpperConverter(uint version) : IConverter
    {
        public int Runs
        {
            get;
            private set;
        }

        public string Id
        {
            get;
        } = "test.upper";

        public uint Version
        {
            get;
        } = version;

        public byte[] Convert(ReadOnlySpan<byte> input)
        {
            Runs++;

            var output = input.ToArray();

            for (var index = 0; index < output.Length; index++)
            {
                if (output[index] is >= (byte)'a' and <= (byte)'z')
                {
                    output[index] -= 32;
                }
            }

            return output;
        }
    }
}
