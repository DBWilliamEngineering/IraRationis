// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly bool VersionedTextureUploadsEnabled =
        Environment.GetEnvironmentVariable("IRX_VERSIONED_TEXTURE_UPLOADS") == "1";

    private static bool PrepareHostTextureUpload(UtilityContext utility, HostTextureCopy plan,
        ReadOnlySpan<byte> source)
    {
        if (utility.Versioned)
        {
            var rented = ArrayPool<byte>.Shared.Rent((int)plan.Bytes);

            try
            {
                var target = rented.AsSpan(0, (int)plan.Bytes);

                target.Clear();

                if (!DetileTextureTexture(plan, source, target))
                {
                    return false;
                }

                // Compare an owned linear snapshot, not mutable guest memory or
                // a mapped write-combined heap. Still copy to the destination GPU
                // texture on every submission: upload reuse is not texture coherence.
                utility.PrepareVersionedUpload(target);

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        var staging = utility.Upload;
        var noRead = default(HostRange);
        var written = default(HostRange);
        var mapped = (void*)null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(staging, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(staging, 9);

        CheckHostResult(map(staging, 0, &noRead, &mapped), "Map host texture staging");

        try
        {
            if (mapped == null)
            {
                return false;
            }

            var target = new Span<byte>(mapped, (int)plan.Bytes);

            target.Clear();

            written.End = (nuint)plan.Bytes;

            return DetileTextureTexture(plan, source, target);
        }
        finally
        {
            unmap(staging, 0, &written);
        }
    }

    private static bool TryHostTextureTransferRange(HostTextureTransferFootprint footprint, ulong bufferBytes,
        out uint rows, out uint rowBytes)
    {
        rows = 0;
        rowBytes = 0;

        if (footprint.Width is 0 or > 16384 || footprint.Height is 0 or > 16384 ||
            footprint.RowPitch == 0 || footprint.RowPitch % 256 != 0 || footprint.Offset % 512 != 0 ||
            footprint.Offset > bufferBytes || !TextureUploadFormat(footprint.Format) ||
            !TryCopyFormat(footprint.Format, out var element, out var block))
        {
            return false;
        }

        var widthBlocks = (footprint.Width + block - 1) / block;
        var heightBlocks = (footprint.Height + block - 1) / block;
        var activeBytes = widthBlocks * element;

        if (activeBytes == 0 || activeBytes > footprint.RowPitch)
        {
            return false;
        }

        // The final row needs only its active bytes, not trailing pitch padding.
        var required = (ulong)(heightBlocks - 1) * footprint.RowPitch + activeBytes;

        if (required > bufferBytes - footprint.Offset)
        {
            return false;
        }

        rows = heightBlocks;
        rowBytes = activeBytes;

        return true;
    }

    internal static void RecordHostTextureTransfer(nint list, nint texture, nint buffer, ulong bufferBytes,
        HostTextureTransferFootprint footprint, bool upload)
    {
        if (list == 0 || texture == 0 || buffer == 0 || texture == buffer ||
            !TryHostTextureTransferRange(footprint, bufferBytes, out _, out _))
        {
            throw new ArgumentException("Invalid host texture transfer footprint.", nameof(footprint));
        }

        var image = new ClearProbeCopyLocation { Resource = texture, Offset = footprint.Subresource };
        var placed = new ClearProbeCopyLocation
        {
            Resource = buffer,
            Kind = 1,
            Offset = footprint.Offset,
            Format = footprint.Format,
            Width = footprint.Width,
            Height = footprint.Height,
            Depth = 1,
            Pitch = footprint.RowPitch
        };
        var copy =
            (delegate* unmanaged[Stdcall]<nint, ClearProbeCopyLocation*, uint, uint, uint, ClearProbeCopyLocation*,
                void*, void>)Slot(list, 16);

        if (upload)
        {
            copy(list, &image, 0, 0, 0, &placed, null);
        }
        else
        {
            copy(list, &placed, 0, 0, 0, &image, null);
        }
    }

    // Extend the existing native resource round-trip probe. Original RGBA8 data,
    // two unequal mip extents, nonzero placement, pitched rows and a real GPU copy.
    private static void VerifyHostTextureTransfers(nint queue)
    {
        const int bytes = 2048;

        HostTextureTransferFootprint[] footprints =
        [
            new(0, 28, 7, 5, 256, 0),
            new(1, 28, 3, 2, 256, 1536)
        ];

        var data = new byte[bytes];

        data.AsSpan().Fill(0xCD);

        foreach (var footprint in footprints)
        {
            if (!TryHostTextureTransferRange(footprint, bytes, out var rows, out var rowBytes))
            {
                throw new InvalidOperationException("R57 valid footprint rejected.");
            }

            for (var row = 0U; row < rows; row++)
            {
                var offset = (int)(footprint.Offset + (ulong)row * footprint.RowPitch);

                for (var index = 0U; index < rowBytes; index++)
                {
                    data[offset + (int)index] = (byte)((footprint.Subresource * 43 + row * 19 + index * 7) & 255);
                }
            }
        }

        var first = footprints[0];

        if (TryHostTextureTransferRange(first with { RowPitch = 255 }, bytes, out _, out _) ||
            TryHostTextureTransferRange(first with { Offset = 1 }, bytes, out _, out _) ||
            TryHostTextureTransferRange(first, 1024, out _, out _) ||
            TryHostTextureTransferRange(first with { Format = 0 }, bytes, out _, out _))
        {
            throw new InvalidOperationException("R57 invalid footprint accepted.");
        }

        var description = new ResourceDescription
        {
            Dimension = 3,
            Width = 7,
            Height = 5,
            DepthOrArraySize = 1,
            MipLevels = 2,
            Format = 28,
            SampleCount = 1
        };
        nint texture = 0, readback = 0;

        try
        {
            CheckHostResult(CreateHostResource(description, 1, 0, &texture), "R57 host texture");
            RequireHostObject(texture, "R57 host texture");

            CheckHostResult(CreateHostResource(HostBufferDescription(bytes), 3, CopyDestination, &readback),
                "R57 host texture readback");
            RequireHostObject(readback, "R57 host texture readback");

            using var utility = AcquireUtilityContext(bytes, 0, versioned: true);

            utility.PrepareVersionedUpload(data);

            var list = utility.List;

            TransitionHostResource(list, texture, 0, CopyDestination);

            foreach (var footprint in footprints)
            {
                RecordHostTextureTransfer(list, texture, utility.Upload, bytes, footprint, upload: true);
            }

            TransitionHostResource(list, texture, CopyDestination, CopySource);

            foreach (var footprint in footprints)
            {
                RecordHostTextureTransfer(list, texture, readback, bytes, footprint, upload: false);
            }

            TransitionHostResource(list, texture, CopySource, 0);

            utility.SubmitAndWait(queue);

            var read = new HostRange { End = bytes };
            var noWrite = default(HostRange);
            var mapped = (void*)null;
            var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
            var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

            CheckHostResult(map(readback, 0, &read, &mapped), "R57 readback map");

            try
            {
                if (mapped == null)
                {
                    throw new InvalidOperationException("R57 readback mapping is null.");
                }

                var result = new ReadOnlySpan<byte>(mapped, bytes);

                foreach (var footprint in footprints)
                {
                    if (!TryHostTextureTransferRange(footprint, bytes, out var rows, out var rowBytes))
                    {
                        throw new InvalidOperationException("R57 footprint invalid during verification.");
                    }

                    for (var row = 0U; row < rows; row++)
                    {
                        var offset = (int)(footprint.Offset + (ulong)row * footprint.RowPitch);
                        var count = (int)rowBytes;

                        if (!result.Slice(offset, count).SequenceEqual(data.AsSpan(offset, count)))
                        {
                            throw new InvalidOperationException("R57 GPU texture readback differs.");
                        }
                    }
                }
            }
            finally
            {
                unmap(readback, 0, &noWrite);
            }

            Console.WriteLine("[graphics-probe] IRX-R57: mips=2; activeBytes=164; readback=verified; invalidFootprints=rejected.");
        }
        finally
        {
            ReleaseHostObject(readback);
            ReleaseHostObject(texture);
        }
    }
}
