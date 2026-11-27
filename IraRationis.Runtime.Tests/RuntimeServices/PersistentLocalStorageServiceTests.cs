// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;
using IraRationis.HLE.Runtime.Services;
using IraRationis.HLE.Runtime.Services.Storage;
using IraRationis.HLE.Runtime.Services.Storage.Models;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal static unsafe class PersistentLocalStorageServiceTests
{
    internal static void Run()
    {
        Require(Marshal.SizeOf<PersistentStorageSpaceInfo>() == 32
                && Marshal.OffsetOf<PersistentStorageSpaceInfo>(nameof(PersistentStorageSpaceInfo.AvailableFreeBytes)) == 0
                && Marshal.OffsetOf<PersistentStorageSpaceInfo>(nameof(PersistentStorageSpaceInfo.TotalFreeBytes)) == 8
                && Marshal.OffsetOf<PersistentStorageSpaceInfo>(nameof(PersistentStorageSpaceInfo.UsedBytes)) == 16
                && Marshal.OffsetOf<PersistentStorageSpaceInfo>(nameof(PersistentStorageSpaceInfo.TotalBytes)) == 24,
            "Persistent storage space information must retain its four-uint64 native ABI.");

        PathBuffersAndInterfaceOwnership();
        FilesPersistAndSpaceReflectsOwnedContents();
        UsageTraversalHasAnExplicitBound();
        FilesystemFailureDoesNotPublishAPartialPath();

        Console.WriteLine("Authored persistent local storage path, space, lifetime, and bounded traversal contracts passed.");
    }

    private static void PathBuffersAndInterfaceOwnership()
    {
        using var fixture = new PersistentStorageFixture();
        var directory = Path.Combine(fixture.DirectoryPath, "persistent-\u03A9");
        var service = new PersistentLocalStorageService(directory, new RuntimeServiceTrace(TextWriter.Null));
        Require(service.TryQuery(PersistentLocalStorageService.InterfaceId, out var address),
            "The known persistent storage interface must resolve.");
        Require(!service.TryQuery(Guid.Empty, out var unknownAddress) && unknownAddress == 0,
            "Unknown provider interfaces must remain unsupported.");

        var methods = *(nint**)address;
        var getSize = (delegate* unmanaged[Stdcall]<nint, nuint*, int>)methods[3];
        var getPath = (delegate* unmanaged[Stdcall]<nint, nuint, byte*, nuint*, int>)methods[4];
        nuint required = 0;

        Require(getSize(address, null) == RuntimeServiceResult.InvalidArgument
                && getSize(address, &required) == RuntimeServiceResult.Success,
            "Path-size queries must reject null outputs and report their required byte count.");

        var expectedPath = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        Require(required == (nuint)Encoding.UTF8.GetByteCount(expectedPath) + 1,
            "Path sizes must include the UTF-8 bytes and terminating NUL.");

        var buffer = new byte[checked((int)required + 16)];
        Array.Fill(buffer, (byte)0xA5);

        fixed (byte* storage = buffer)
        {
            nuint used = 99;
            Require(getPath(address, required - 1, storage + 8, &used) == RuntimeServiceResult.Failure
                    && used == 0 && buffer.All(value => value == 0xA5),
                "A short Xbox path buffer must return E_FAIL without a partial path or canary overwrite.");
            Require(getPath(address, required, null, &used) == RuntimeServiceResult.InvalidArgument
                    && getPath(address, required, storage + 8, &used) == RuntimeServiceResult.Success
                    && used == required,
                "Path retrieval must reject null buffers and accept exactly the advertised capacity.");
            Require(buffer.AsSpan(0, 8).IndexOfAnyExcept((byte)0xA5) < 0
                    && buffer.AsSpan(8 + (int)required).IndexOfAnyExcept((byte)0xA5) < 0
                    && buffer[8 + (int)required - 1] == 0
                    && Encoding.UTF8.GetString(buffer, 8, (int)required - 1) == expectedPath,
                "The service must publish only the exact NUL-terminated path within the caller's buffer.");
            Require(getPath(address, required, storage + 8, null) == RuntimeServiceResult.Success,
                "The title's observed null path-used pointer must remain valid.");
        }

        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)methods[0];
        var unknownInterface = Guid.Empty;
        nint queried = 123;

        Require(queryInterface(address, &unknownInterface, &queried) == RuntimeServiceResult.NoInterface && queried == 0,
            "IUnknown queries must not manufacture support for unrelated interfaces.");

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)methods[2];
        _ = release(address);
        _ = release(address);
        Require(getSize(address, &required) == RuntimeServiceResult.Success,
            "The service anchor must keep cached title pointers valid after guest releases.");

        byte authoredAsyncBlock = 0;
        var prompt = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)methods[6];
        var promptResult = (delegate* unmanaged[Stdcall]<nint, nint, int>)methods[7];

        Require(prompt(address, 1024, (nint)(&authoredAsyncBlock)) == RuntimeServiceResult.NotImplemented
                && promptResult(address, (nint)(&authoredAsyncBlock)) == RuntimeServiceResult.NotImplemented,
            "Unsupported storage UI must return failure without promising an asynchronous completion.");
    }

    private static void FilesPersistAndSpaceReflectsOwnedContents()
    {
        using var fixture = new PersistentStorageFixture();
        var directory = Path.Combine(fixture.DirectoryPath, "persistent");
        var trace = new RuntimeServiceTrace(TextWriter.Null);
        var service = new PersistentLocalStorageService(directory, trace, 65536);
        File.WriteAllBytes(Path.Combine(directory, "cache.bin"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        File.WriteAllBytes(Path.Combine(directory, "nested", "cache.bin"), [4, 5, 6, 7, 8]);

        var reopened = new PersistentLocalStorageService(directory, trace, 65536);
        Require(File.ReadAllBytes(Path.Combine(directory, "cache.bin")).SequenceEqual(new byte[] { 1, 2, 3 }),
            "Recreating a storage service must preserve existing title contents.");
        Require(reopened.TryQuery(PersistentLocalStorageService.InterfaceId, out var address), "Reopened storage must resolve.");

        var getSpace = (delegate* unmanaged[Stdcall]<nint, PersistentStorageSpaceInfo*, int>)(*(nint**)address)[5];
        var space = default(PersistentStorageSpaceInfo);

        Require(getSpace(address, null) == RuntimeServiceResult.InvalidArgument
                && getSpace(address, &space) == RuntimeServiceResult.Success,
            "Space queries must reject null output and report the actual owned directory.");
        Require(space.UsedBytes == 8 && space.TotalBytes == 65536
                && space.AvailableFreeBytes <= space.TotalFreeBytes
                && space.TotalFreeBytes <= space.TotalBytes - space.UsedBytes,
            "Space results must include nested title files and respect the reported capacity and host availability.");

        var overCapacity = new PersistentStorageDirectory(directory, 4).ReadSpaceInfo();
        Require(overCapacity.UsedBytes == 8 && overCapacity.AvailableFreeBytes == 0 && overCapacity.TotalFreeBytes == 0,
            "External writes beyond an advisory capacity must be visible as usage, without unsigned free-space underflow.");
        GC.KeepAlive(service);
    }

    private static void UsageTraversalHasAnExplicitBound()
    {
        using var fixture = new PersistentStorageFixture();
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "first"));
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "second"));
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "third"));
        var directory = new PersistentStorageDirectory(fixture.DirectoryPath, maximumEntries: 2);
        var rejected = false;

        try
        {
            _ = directory.ReadSpaceInfo();
        }
        catch (IOException)
        {
            rejected = true;
        }

        Require(rejected, "The storage usage bound must count directories as well as files and reject incomplete snapshots.");
    }

    private static void FilesystemFailureDoesNotPublishAPartialPath()
    {
        using var fixture = new PersistentStorageFixture();
        var directory = Path.Combine(fixture.DirectoryPath, "blocked");
        var service = new PersistentLocalStorageService(directory, new RuntimeServiceTrace(TextWriter.Null));
        Require(service.TryQuery(PersistentLocalStorageService.InterfaceId, out var address), "Authored blocked storage must resolve.");
        Directory.Delete(directory);
        File.WriteAllText(directory, "authored obstruction");

        var getPath = (delegate* unmanaged[Stdcall]<nint, nuint, byte*, nuint*, int>)(*(nint**)address)[4];
        Span<byte> buffer = stackalloc byte[1024];
        buffer.Fill(0xA5);

        fixed (byte* output = buffer)
        {
            nuint used = 99;
            Require(getPath(address, (nuint)buffer.Length, output, &used) < 0
                    && used == 0 && buffer.IndexOfAnyExcept((byte)0xA5) < 0,
                "A filesystem failure must remain an HRESULT failure with no partial path publication.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
