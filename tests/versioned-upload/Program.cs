// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures;

namespace VersionedUpload.Contracts;

internal static class Program
{
    private static int Main()
    {
        TestInitialAndPartialUpload();

        TestWritesDuringSubmission();

        TestCancellationAndInvalidTransitions();

        TestPitchedPlaneCopy();

        Console.WriteLine("IRX-R51 CPU contracts passed.");

        return 0;
    }

    private static void TestInitialAndPartialUpload()
    {
        var buffer = new VersionedUploadBuffer(16);
        var initial = Capture(buffer);

        Require(initial.Offset == 0 && initial.Bytes.Length == 16, "Initial full upload");

        Require(initial.Bytes.SequenceEqual(new byte[16]), "Initial zero contents");

        Require(buffer.CompletedVersion == 0, "Initial host version unknown");

        Complete(buffer, initial);

        Require(buffer.Capture() is null, "Clean buffer skips upload");

        buffer.Write(4, [1, 2, 3]);

        var partial = Capture(buffer);

        Require(partial.Offset == 4 && partial.Bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "Exact partial upload");

        Require(buffer.CompletedVersion == initial.Version, "Capture is not completion");

        Complete(buffer, partial);

        buffer.Write(4, [1, 2, 3]);

        buffer.Write(16, []);

        Require(buffer.Capture() is null, "Identical and empty writes do not upload");

        Require(buffer.CompletedVersion == partial.Version, "Partial version completed");
    }

    private static void TestWritesDuringSubmission()
    {
        var buffer = new VersionedUploadBuffer(16);
        var initial = Capture(buffer);

        buffer.MarkSubmitted(initial);

        buffer.Write(5, [9]);

        Require(initial.Bytes[5] == 0, "Snapshot remains immutable after write");

        Require(buffer.CompletedVersion == 0, "Submission does not complete version");

        buffer.CompleteAfterFenceWait(initial);

        var next = Capture(buffer);

        Require(next.Offset == 5 && next.Bytes.Length == 1 && next.Bytes[0] == 9, "Late write stays dirty");

        Require(next.Version > initial.Version, "Versions increase");

        Complete(buffer, next);

        Require(buffer.Capture() is null, "Late write acknowledged");
    }

    private static void TestCancellationAndInvalidTransitions()
    {
        var buffer = new VersionedUploadBuffer(16);
        var initial = Capture(buffer);

        Complete(buffer, initial);

        buffer.Write(2, [7]);

        var cancelled = Capture(buffer);

        buffer.Write(8, [9]);

        buffer.Cancel(cancelled);

        var retry = Capture(buffer);

        Require(retry.Offset == 2 && retry.Bytes.Length == 7, "Cancelled and newer ranges coalesce");

        Require(retry.Bytes[0] == 7 && retry.Bytes[^1] == 9, "Retry retains latest contents");

        Expect<InvalidOperationException>(() => buffer.MarkSubmitted(cancelled));

        Expect<InvalidOperationException>(() => buffer.CompleteAfterFenceWait(retry));

        Expect<InvalidOperationException>(() => buffer.Capture());

        Expect<ArgumentOutOfRangeException>(() => buffer.Write(-1, [1]));

        Expect<ArgumentOutOfRangeException>(() => buffer.Write(16, [1]));

        var foreign = new VersionedUploadBuffer(16);
        var foreignSnapshot = Capture(foreign);

        Expect<InvalidOperationException>(() => buffer.MarkSubmitted(foreignSnapshot));

        foreign.Cancel(foreignSnapshot);

        buffer.MarkSubmitted(retry);

        Expect<InvalidOperationException>(() => buffer.MarkSubmitted(retry));

        Expect<InvalidOperationException>(() => buffer.Cancel(retry));

        buffer.CompleteAfterFenceWait(retry);

        Expect<InvalidOperationException>(() => buffer.CompleteAfterFenceWait(retry));

        Require(buffer.Capture() is null, "Retry completes all dirty ranges");
    }

    private static void TestPitchedPlaneCopy()
    {
        const int width = 320;
        const int lumaRows = 180;
        const int chromaRows = lumaRows / 2;
        const int pitch = 512;
        const int chromaOffset = pitch * lumaRows;

        var luma = new byte[width * lumaRows];
        var chroma = new byte[width * chromaRows];
        var upload = new byte[pitch * (lumaRows + chromaRows)];

        Array.Fill(luma, (byte)0x11);

        Array.Fill(chroma, (byte)0x22);

        Array.Fill(upload, (byte)0xCC);

        PitchedPlaneCopy.Copy(luma, width, upload, 0, pitch, width, lumaRows);

        PitchedPlaneCopy.Copy(chroma, width, upload, chromaOffset, pitch, width, chromaRows);

        Require(upload[0] == 0x11 && upload[chromaOffset] == 0x22, "Planes copied independently");

        Require(upload[width] == 0xCC && upload[chromaOffset + width] == 0xCC, "Padding preserved");

        Require(upload[^1] == 0xCC && upload[upload.Length - pitch + width - 1] == 0x22,
            "Final chroma row copied without touching padding");

        var before = upload.ToArray();

        Expect<ArgumentException>(() => PitchedPlaneCopy.Copy(chroma, width, upload, chromaOffset, pitch, width, lumaRows));

        Expect<ArgumentException>(() => PitchedPlaneCopy.Copy(chroma, width, upload, upload.Length, pitch, width, chromaRows));

        Require(upload.AsSpan().SequenceEqual(before), "Invalid copies do not partially modify upload memory");
    }

    private static VersionedUploadBufferSnapshot Capture(VersionedUploadBuffer buffer)
    {
        return buffer.Capture() ?? throw new InvalidOperationException("Expected dirty snapshot.");
    }

    private static void Complete(VersionedUploadBuffer buffer, VersionedUploadBufferSnapshot snapshot)
    {
        buffer.MarkSubmitted(snapshot);

        buffer.CompleteAfterFenceWait(snapshot);
    }

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
