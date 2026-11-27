// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    // This diagnostic owns all input bytes. It does not infer writes through guest pointers.
    private static void VerifyVersionedUpload(nint device, nint queue, nint allocator,
        nint target, nint upload, nint readback, ReadOnlySpan<byte> initial)
    {
        var expected = initial.ToArray();
        var buffer = new VersionedUploadBuffer(expected.Length);

        buffer.Write(0, expected);

        var full = buffer.Capture() ?? throw new InvalidOperationException("Initial upload was not scheduled.");

        ExecuteVersionedUpload(device, queue, allocator, target, upload, readback, buffer, full, expected);

        if (buffer.Capture() is not null || buffer.CompletedVersion != full.Version)
        {
            throw new InvalidOperationException("R51 initial completion or no-op detection failed.");
        }

        const int changeOffset = 128;
        const int changeLength = 64;

        var change = expected.AsSpan(changeOffset, changeLength);

        for (var index = 0; index < change.Length; index++)
        {
            change[index] ^= 0xFF;
        }

        buffer.Write(changeOffset, change);

        var partial = buffer.Capture() ?? throw new InvalidOperationException("Partial upload was not scheduled.");

        if (partial.Offset != changeOffset || partial.Bytes.Length != changeLength)
        {
            throw new InvalidOperationException("R51 dirty range differs from the explicit write.");
        }

        ExecuteVersionedUpload(device, queue, allocator, target, upload, readback, buffer, partial, expected);

        buffer.Write(changeOffset, change);

        if (buffer.Capture() is not null || buffer.CompletedVersion != partial.Version)
        {
            throw new InvalidOperationException("R51 unchanged write triggered a redundant upload.");
        }

        Console.WriteLine(
            $"[graphics-probe] IRX-R51: fullBytes={full.Bytes.Length}; partialBytes={partial.Bytes.Length}; " +
            $"uploads=2; unchangedUploads=0; completedVersion={buffer.CompletedVersion}; readback=verified.");
    }

    private static void ExecuteVersionedUpload(nint device, nint queue, nint allocator,
        nint target, nint upload, nint readback, VersionedUploadBuffer buffer,
        VersionedUploadBufferSnapshot snapshot, ReadOnlySpan<byte> expected)
    {
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, 9);
        var noRead = default(HostRange);
        var mapped = (void*)null;
        var list = nint.Zero;
        var submitted = false;
        var completed = false;

        try
        {
            CheckHostResult(map(upload, 0, &noRead, &mapped), "R51 map upload");

            try
            {
                if (mapped == null)
                {
                    throw new InvalidOperationException("R51 upload map returned null.");
                }

                snapshot.Bytes.CopyTo(new Span<byte>(mapped, snapshot.Bytes.Length));
            }
            finally
            {
                var written = new HostRange { End = (nuint)snapshot.Bytes.Length };

                unmap(upload, 0, &written);
            }

            var listId = HostListId;
            var createList =
                (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12);

            CheckHostResult(createList(device, 0, 0, allocator, 0, &listId, &list), "R51 create copy list");

            if (list == 0)
            {
                throw new InvalidOperationException("R51 command list is null.");
            }

            var copy = (delegate* unmanaged[Stdcall]<nint, nint, ulong, nint, ulong, ulong, void>)Slot(list, 15);

            TransitionHostResource(list, target, 0, CopyDestination);

            copy(list, target, (ulong)snapshot.Offset, upload, 0, (ulong)snapshot.Bytes.Length);

            TransitionHostResource(list, target, CopyDestination, CopySource);

            // Read back the whole destination to verify that untouched bytes remain intact.
            copy(list, readback, 0, target, 0, (ulong)expected.Length);

            TransitionHostResource(list, target, CopySource, 0);

            CheckHostResult(((delegate* unmanaged[Stdcall]<nint, int>)Slot(list, 9))(list), "R51 close copy list");

            var execute = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10);

            buffer.MarkSubmitted(snapshot);

            submitted = true;

            execute(queue, 1, &list);

            WaitForHostCompletion(queue);

            completed = true;

            buffer.CompleteAfterFenceWait(snapshot);

            var read = new HostRange { End = (nuint)expected.Length };
            var mapReadback = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
            var unmapReadback = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

            mapped = null;

            CheckHostResult(mapReadback(readback, 0, &read, &mapped), "R51 map readback");

            try
            {
                if (mapped == null || !new ReadOnlySpan<byte>(mapped, expected.Length).SequenceEqual(expected))
                {
                    throw new InvalidOperationException("R51 versioned GPU buffer bytes differ.");
                }
            }
            finally
            {
                unmapReadback(readback, 0, &noRead);
            }
        }
        catch (Exception exception)
        {
            if (submitted && !completed)
            {
                Environment.FailFast("R51 cannot prove GPU completion; refusing unsafe resource release.", exception);
            }

            if (!submitted)
            {
                buffer.Cancel(snapshot);
            }

            throw;
        }
        finally
        {
            if (list != 0)
            {
                _ = HostRelease(list);
            }
        }
    }
}
