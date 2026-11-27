// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.Resources;

/// <summary>
///     Single-owner, explicitly written CPU buffer and its host upload state.
///     One snapshot may be outstanding; writes during that lifetime remain dirty.
///     This does not track raw pointer writes, aliases, or GPU-authored contents.
/// </summary>
internal sealed class VersionedUploadBuffer
{
    //TODO: Damon William William -> Integrate raw guest writes and physical aliases before using versions to skip general title uploads.
    private const int MaximumBytes = 16 * 1024 * 1024;
    private readonly byte[] _data;

    private readonly int _owner = Environment.CurrentManagedThreadId;
    private ulong _completedVersion;
    private int _dirtyEnd;
    private int _dirtyStart;
    private VersionedUploadBufferSnapshot? _pending;
    private bool _submitted;
    private ulong _version = 1;

    internal VersionedUploadBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaximumBytes);

        _data = new byte[length];

        _dirtyEnd = length;
    }

    internal ulong CompletedVersion
    {
        get
        {
            CheckOwner();

            return _completedVersion;
        }
    }

    internal void Write(int offset, ReadOnlySpan<byte> source)
    {
        CheckOwner();

        if (offset < 0 || offset > _data.Length || source.Length > _data.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Write exceeds owned buffer.");
        }

        var destination = _data.AsSpan(offset, source.Length);

        if (source.SequenceEqual(destination))
        {
            return;
        }

        var nextVersion = checked(_version + 1);

        source.CopyTo(destination);

        _version = nextVersion;

        MergeDirty(offset, offset + source.Length);
    }

    internal VersionedUploadBufferSnapshot? Capture()
    {
        CheckOwner();

        if (_pending is not null)
        {
            throw new InvalidOperationException("An upload snapshot is already outstanding.");
        }

        if (_dirtyStart == _dirtyEnd)
        {
            return null;
        }

        var bytes = _data.AsSpan(_dirtyStart, _dirtyEnd - _dirtyStart).ToArray();
        var snapshot = new VersionedUploadBufferSnapshot(_version, _dirtyStart, bytes);

        _pending = snapshot;

        _dirtyStart = 0;

        _dirtyEnd = 0;

        return snapshot;
    }

    internal void MarkSubmitted(VersionedUploadBufferSnapshot snapshot)
    {
        CheckPending(snapshot);

        if (_submitted)
        {
            throw new InvalidOperationException("Snapshot was already submitted.");
        }

        _submitted = true;
    }

    /// <summary>Only valid before native queue submission.</summary>
    internal void Cancel(VersionedUploadBufferSnapshot snapshot)
    {
        CheckPending(snapshot);

        if (_submitted)
        {
            throw new InvalidOperationException("Cannot cancel an in-flight upload.");
        }

        MergeDirty(snapshot.Offset, snapshot.Offset + snapshot.Bytes.Length);

        _pending = null;
    }

    /// <summary>The caller must have proved completion of this snapshot's GPU work.</summary>
    internal void CompleteAfterFenceWait(VersionedUploadBufferSnapshot snapshot)
    {
        CheckPending(snapshot);

        if (!_submitted)
        {
            throw new InvalidOperationException("Cannot complete an unsubmitted snapshot.");
        }

        _completedVersion = snapshot.Version;

        _pending = null;
        _submitted = false;
    }

    private void MergeDirty(int start, int end)
    {
        if (_dirtyStart == _dirtyEnd)
        {
            _dirtyStart = start;

            _dirtyEnd = end;

            return;
        }

        _dirtyStart = Math.Min(_dirtyStart, start);
        _dirtyEnd = Math.Max(_dirtyEnd, end);
    }

    private void CheckPending(VersionedUploadBufferSnapshot snapshot)
    {
        CheckOwner();

        ArgumentNullException.ThrowIfNull(snapshot);

        if (!ReferenceEquals(_pending, snapshot))
        {
            throw new InvalidOperationException("Snapshot does not belong to the active upload.");
        }
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("Upload state is owned by its creating thread.");
        }
    }
}
