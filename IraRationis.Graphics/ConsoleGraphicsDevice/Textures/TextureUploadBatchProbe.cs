// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static void VerifyTextureUploadBatch(nint queue)
    {
        const int rowPitch = 256;
        const int secondOffset = 512;
        const int readbackBytes = secondOffset + rowPitch;
        var firstFootprint = new HostTextureTransferFootprint(0, 28, 1, 1, rowPitch, 0);
        var secondFootprint = firstFootprint with { Offset = secondOffset };

        nint first = 0, second = 0, readback = 0;
        try
        {
            var description = RasterTexture(1, 1, 0);
            CheckHostResult(CreateHostResource(description, 1, 0, &first), "first batched upload target");
            CheckHostResult(CreateHostResource(description, 1, 0, &second), "second batched upload target");
            CheckHostResult(CreateHostResource(HostBufferDescription(readbackBytes), 3, CopyDestination, &readback),
                "batched upload readback");
            RequireHostObject(first, "first batched upload target");
            RequireHostObject(second, "second batched upload target");
            RequireHostObject(readback, "batched upload readback");

            using var firstUpload = AcquireUtilityContext(rowPitch, 0, true);
            using var secondUpload = AcquireUtilityContext(rowPitch, 0, true);
            using var replacement = AcquireUtilityContext(rowPitch, 0, true);
            using var ordered = new OrderedTextureUploads(queue, true);

            RecordTextureUploadProbe(firstUpload, first, firstFootprint, [16, 32, 48, 64]);
            RecordTextureUploadProbe(secondUpload, second, firstFootprint, [80, 96, 112, 128]);
            RecordTextureUploadProbe(replacement, first, firstFootprint, [144, 160, 176, 192]);
            var previousSavings = Interlocked.Read(ref _textureUploadSubmitsSaved);
            ordered.Submit(firstUpload, first);
            ordered.Submit(secondUpload, second);
            ordered.Submit(replacement, first);
            ordered.SubmitBeforeConsumer();

            using var consumer = AcquireUtilityContext(0, 0);
            TransitionHostResource(consumer.List, first, 0, CopySource);
            TransitionHostResource(consumer.List, second, 0, CopySource);
            RecordHostTextureTransfer(consumer.List, first, readback, readbackBytes, firstFootprint, false);
            RecordHostTextureTransfer(consumer.List, second, readback, readbackBytes, secondFootprint, false);
            consumer.SubmitAndWait(queue);
            ordered.CompleteAfterFence();

            var pixels = ReadRasterPixels(readback, 1, 3, rowPitch);
            if (Interlocked.Read(ref _textureUploadSubmitsSaved) != previousSavings + 1 ||
                !pixels.AsSpan(0, 4).SequenceEqual(new byte[] { 144, 160, 176, 192 }) ||
                !pixels.AsSpan(8, 4).SequenceEqual(new byte[] { 80, 96, 112, 128 }))
            {
                throw new InvalidOperationException("Batched texture upload readback or repeated-target order failed.");
            }

            Console.WriteLine(
                "[graphics-probe] Batched texture upload GPU readback passed (independent targets and repeated-target order).");
        }
        finally
        {
            ReleaseHostObject(readback);
            ReleaseHostObject(second);
            ReleaseHostObject(first);
        }
    }

    private static void RecordTextureUploadProbe(UtilityContext utility, nint target,
        HostTextureTransferFootprint footprint, ReadOnlySpan<byte> pixel)
    {
        var bytes = new byte[footprint.RowPitch];
        pixel.CopyTo(bytes);
        utility.PrepareVersionedUpload(bytes);
        TransitionHostResource(utility.List, target, 0, CopyDestination);
        RecordHostTextureTransfer(utility.List, target, utility.Upload, (ulong)bytes.Length, footprint, true);
        TransitionHostResource(utility.List, target, CopyDestination, 0);
    }
}
