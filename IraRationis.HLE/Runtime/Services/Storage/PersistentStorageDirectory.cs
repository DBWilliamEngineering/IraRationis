// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Storage.Models;

namespace IraRationis.HLE.Runtime.Services.Storage;

internal sealed class PersistentStorageDirectory
{
    private const int DefaultMaximumEntries = 4096;
    private readonly ulong _capacityBytes;
    private readonly int _maximumEntries;

    internal PersistentStorageDirectory(string directory, ulong capacityBytes = 0,
        int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);

        Path = System.IO.Path.GetFullPath(directory);
        _capacityBytes = capacityBytes;
        _maximumEntries = maximumEntries;

        EnsureExists();
    }

    internal string Path
    {
        get;
    }

    internal void EnsureExists()
    {
        Directory.CreateDirectory(Path);
    }

    internal PersistentStorageSpaceInfo ReadSpaceInfo()
    {
        EnsureExists();

        var usedBytes = MeasureUsedBytes();

        if (PersistentStorageNativeMethods.GetDiskFreeSpace(Path, out var availableBytes, out var totalBytes,
                out var totalFreeBytes) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var capacity = _capacityBytes == 0 ? totalBytes : Math.Min(_capacityBytes, totalBytes);
        var remainingCapacity = capacity > usedBytes ? capacity - usedBytes : 0;
        var remainingBytes = Math.Min(remainingCapacity, totalFreeBytes);

        return new PersistentStorageSpaceInfo
        {
            AvailableFreeBytes = Math.Min(availableBytes, remainingBytes),
            TotalFreeBytes = remainingBytes,
            UsedBytes = usedBytes,
            TotalBytes = capacity
        };
    }

    private ulong MeasureUsedBytes()
    {
        // O(entries), capped at 4096 by default; only this title's directory is traversed.
        //TODO: Damon William -> Replace bounded scans with accounting once guest file writes share one ownership boundary.
        var pending = new Stack<string>();
        pending.Push(Path);
        var visitedEntries = 0;
        var usedBytes = 0UL;

        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++visitedEntries > _maximumEntries)
                {
                    throw new IOException("Persistent storage usage exceeded the bounded directory scan.");
                }

                var attributes = File.GetAttributes(entry);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    usedBytes = checked(usedBytes + (ulong)new FileInfo(entry).Length);
                }
            }
        }

        return usedBytes;
    }
}
