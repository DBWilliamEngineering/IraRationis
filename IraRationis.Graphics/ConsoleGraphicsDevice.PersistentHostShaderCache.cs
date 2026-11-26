// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    // Only locally generated host bytecode. This directory is a trusted per-user
    // cache, not a boundary for accepting untrusted shader binaries.
    private const int R64MaximumEntryBytes = 16 * 1024 * 1024;
    private const long R64MaximumDiskBytes = 128L << 20;
    private const int R64MaximumDiskEntries = 256;
    private const int R64HeaderBytes = 76;
    private static readonly bool R64DiskCacheEnabled =
        Environment.GetEnvironmentVariable("IRX_HOST_SHADER_DISK_CACHE") == "1";
    private static readonly Lazy<byte[]?> R64CompilerIdentity = new(ReadR64CompilerIdentity);

    private static byte[]? CompileR64PersistentShader(string source, string profile, TextWriter report)
    {
        if (!R64DiskCacheEnabled || !OperatingSystem.IsWindows() || source.Length > 1024 * 1024 ||
            R64CompilerIdentity.Value is not { } compiler)
        {
            return CompileR64ShaderUncached(source, profile, report);
        }

        var directory = Environment.GetEnvironmentVariable("IRX_HOST_SHADER_CACHE_DIR");

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IraRationis", "cache", "host-shaders-v1");
        }

        // Version, entry point, flags, profile, compiler binary and exact compiler input.
        // The schema must change if any compilation contract changes.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData("IRX-host-dxbc-v1\0main\0flags=00008800,00000000\0"u8);
        hash.AppendData(compiler);
        hash.AppendData(Encoding.ASCII.GetBytes(profile + "\0"));
        hash.AppendData(Encoding.UTF8.GetBytes(source));

        var key = hash.GetHashAndReset();
        var path = Path.Combine(directory, Convert.ToHexString(key) + ".irxbc");
        var cached = ReadR64Bytecode(path, key);

        if (cached is not null)
        {
            TraceR24($"[graphics] IRX-R64-DISK: hit; profile={profile}; bytes={cached.Length}.");

            return cached;
        }

        var bytes = CompileR64ShaderUncached(source, profile, report);

        if (bytes is not null && bytes.Length <= R64MaximumEntryBytes)
        {
            if (StoreR64Bytecode(directory, path, key, bytes))
            {
                TraceR24($"[graphics] IRX-R64-DISK: stored; profile={profile}; bytes={bytes.Length}.");
            }
        }

        return bytes;
    }

    private static byte[]? ReadR64CompilerIdentity()
    {
        try
        {
            // The matching D3DCompile import is restricted to System32.
            using var stream = File.OpenRead(Path.Combine(Environment.SystemDirectory, "d3dcompiler_47.dll"));

            return SHA256.HashData(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.Security.SecurityException)
        {
            return null;
        }
    }

    private static byte[]? ReadR64Bytecode(string path, ReadOnlySpan<byte> key)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);

            if (stream.Length <= R64HeaderBytes || stream.Length > R64HeaderBytes + R64MaximumEntryBytes)
            {
                return null;
            }

            Span<byte> header = stackalloc byte[R64HeaderBytes];

            stream.ReadExactly(header);

            var length = BinaryPrimitives.ReadInt32LittleEndian(header[72..]);

            if (!header[..8].SequenceEqual("IRXBC001"u8) || !header.Slice(8, 32).SequenceEqual(key) ||
                length <= 0 || length > R64MaximumEntryBytes || stream.Length != R64HeaderBytes + (long)length)
            {
                return null;
            }

            var bytes = new byte[length];

            stream.ReadExactly(bytes);

            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(header.Slice(40, 32)) || bytes.Length < 32 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4)) != (uint)bytes.Length)
            {
                return null;
            }

            var container = Shaders.ShaderContainer.Parse(bytes);

            return container.Part("SHEX") is not null || container.Part("SHDR") is not null ? bytes : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              InvalidDataException or ArgumentException or OverflowException or
                                              System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool StoreR64Bytecode(string directory, string path, ReadOnlySpan<byte> key, byte[] bytes)
    {
        string? temporary = null;

        try
        {
            _ = Directory.CreateDirectory(directory);

            // Exclusive lock across cooperating processes. Contention or disk errors
            // simply skip persistence; never block native execution waiting for cache I/O.
            using var lease = new FileStream(Path.Combine(directory, "write.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);

            var entries = new List<FileInfo>();
            var total = 0L;

            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.irxbc"))
            {
                if (file.Name.Length != 70 || !IsR64Hex(file.Name.AsSpan(0, 64)) ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                entries.Add(file);
                total += file.Length;
            }

            // Oldest writes first. This is bounded FIFO-style eviction, not a disk LRU.
            entries.Sort(static (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));

            var charge = R64HeaderBytes + (long)bytes.Length;
            var count = entries.Count;

            foreach (var entry in entries)
            {
                if (count < R64MaximumDiskEntries && total <= R64MaximumDiskBytes - charge)
                {
                    break;
                }

                var length = entry.Length;

                entry.Delete();

                total -= length;
                count--;
            }

            if (count >= R64MaximumDiskEntries || total > R64MaximumDiskBytes - charge)
            {
                return false;
            }

            temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");

            Span<byte> header = stackalloc byte[R64HeaderBytes];

            "IRXBC001"u8.CopyTo(header);
            key.CopyTo(header[8..]);
            SHA256.HashData(bytes, header.Slice(40, 32));
            BinaryPrimitives.WriteInt32LittleEndian(header[72..], bytes.Length);

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(header);
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                      System.Security.SecurityException)
                {
                    // Best-effort temporary-file cleanup.
                }
            }
        }
    }

    private static bool IsR64Hex(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
