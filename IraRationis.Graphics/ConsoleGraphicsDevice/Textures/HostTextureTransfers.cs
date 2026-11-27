// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures;
using IraRationis.Graphics.Textures.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

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

            written.End = (nuint)plan.Bytes;

            // The copy footprint reads just the active texels or compressed blocks in each row.
            return DetileTextureTexture(plan, source, new Span<byte>(mapped, (int)plan.Bytes));
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
    private static void VerifyHostTextureTransfers(nint queue, bool deferUpload = false, uint storageFormat = 28,
        uint copyFormat = 28)
    {
        const int bytes = 2048;

        HostTextureTransferFootprint[] footprints =
        [
            new(0, copyFormat, 7, 5, 256, 0),
            new(1, copyFormat, 3, 2, 256, 1536)
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
            Format = storageFormat,
            SampleCount = 1
        };

        using var utility = AcquireUtilityContext(bytes, 0, true);
        utility.PrepareVersionedUpload(data);
        VerifyHostTextureRoundTrip(queue, description, footprints, data, utility, deferUpload);

        Console.WriteLine(storageFormat != 28
            ? $"[graphics-probe] R77 color storage readback passed: storage={storageFormat}; footprint={copyFormat}."
            : deferUpload
                ? "[graphics-probe] R76 ordered texture upload and subsequent same-queue readback passed."
                : "[graphics-probe] IRX-R57: mips=2; activeBytes=164; readback=verified; invalidFootprints=rejected.");
    }

    private static void VerifyTiledBc3Transfer(nint queue)
    {
        const uint linearFormat = (uint)DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM;
        const uint srgbFormat = (uint)DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM_SRGB;
        const uint typelessFormat = (uint)DXGI_FORMAT.DXGI_FORMAT_BC3_TYPELESS;
        const uint rowPitch = 128 * 16;
        const int bytes = (int)rowPitch * 117;

        var description = new ResourceDescription
        {
            Dimension = 3,
            Width = 512,
            Height = 468,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = linearFormat,
            SampleCount = 1,
            Layout = 0x105,
            Flags = 0x300000
        };

        var typeless = description;
        typeless.Format = typelessFormat;

        if (!HostTextureViewFormats.IsSampledCompatible(typeless, linearFormat) ||
            !HostTextureViewFormats.IsSampledCompatible(typeless, srgbFormat) ||
            HostTextureViewFormats.IsSampledCompatible(description, srgbFormat) ||
            !HostTextureViewFormats.TryGetColorCopyFormat(typelessFormat, out var copyFormat)
            || copyFormat != linearFormat)
        {
            throw new InvalidOperationException("BC3 storage and typed view compatibility regressed.");
        }

        var footprint = new HostTextureTransferFootprint(0, linearFormat, 512, 468, rowPitch, 0);
        var partialEdgeFootprint = footprint with { Width = 509, Height = 465 };

        if (!TryHostTextureTransferRange(partialEdgeFootprint, bytes, out var rows, out var rowBytes) ||
            rows != 117 || rowBytes != rowPitch ||
            TryHostTextureTransferRange(partialEdgeFootprint, bytes - 1, out _, out _))
        {
            throw new InvalidOperationException("BC3 transfer did not account for partially covered edge blocks.");
        }

        if (!SupportsHostTextureSampling(linearFormat))
        {
            Console.WriteLine(
                "[graphics-probe] BC3 footprint and view contracts passed (CPU only); GPU format unavailable.");

            return;
        }

        if (!TryPlanTextureTexture(description, out var plan) || plan == null ||
            plan.Bytes != bytes || plan.Pitches.Length != 1 || plan.Pitches[0] != rowPitch || plan.Offsets[0] != 0)
        {
            throw new InvalidOperationException(
                "Tiled BC3 texture upload footprint differs from the authored block extent.");
        }

        var linear = new byte[bytes];
        new Random(0x105).NextBytes(linear);
        var tiled = new byte[checked((int)plan.Layout.Size)];
        WriteAuthoredTiledTexture(description, linear, tiled, rowPitch);

        using var utility = AcquireUtilityContext(bytes, 0);

        if (!PrepareHostTextureUpload(utility, plan, tiled))
        {
            throw new InvalidOperationException("Tiled BC3 texture upload preparation failed.");
        }

        description.Layout = 0;
        description.Flags = 0;
        VerifyHostTextureRoundTrip(queue, description, [footprint], linear, utility, true);
        Console.WriteLine(
            "[graphics-probe] Tiled BC3 blocks passed ordered GPU upload and readback (512x468, layout 0x105).");
    }

    private static void WriteAuthoredTiledTexture(ResourceDescription description, byte[] linear, byte[] tiled,
        uint rowPitch)
    {
        var create = (delegate* unmanaged[Stdcall]<ResourceDescription*, nint*, int>)TextureComputer.CreateAddress;
        nint computer = 0;

        CheckHostResult(create(&description, &computer), "authored texture computer creation");
        RequireHostObject(computer, "authored texture computer");

        try
        {
            var tile = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint, byte*, uint, uint, int>)Slot(computer, 8);

            fixed (byte* input = linear, output = tiled)
            {
                CheckHostResult(tile(computer, output, 0, 0, input, rowPitch, 0), "authored texture tiling");
            }
        }
        finally
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(computer, 1))(computer);
        }
    }

    private static void VerifyHostTextureRoundTrip(nint queue, ResourceDescription description,
        ReadOnlySpan<HostTextureTransferFootprint> footprints, ReadOnlySpan<byte> expected, UtilityContext utility,
        bool deferUpload)
    {
        var bytes = checked((ulong)expected.Length);
        nint texture = 0, readback = 0;

        try
        {
            CheckHostResult(CreateHostResource(description, 1, 0, &texture), "probe host texture");
            RequireHostObject(texture, "probe host texture");

            CheckHostResult(CreateHostResource(HostBufferDescription(bytes), 3, CopyDestination, &readback),
                "probe host texture readback");
            RequireHostObject(readback, "probe host texture readback");

            var list = utility.List;

            TransitionHostResource(list, texture, 0, CopyDestination);

            foreach (var footprint in footprints)
            {
                RecordHostTextureTransfer(list, texture, utility.Upload, bytes, footprint, true);
            }

            if (deferUpload)
            {
                TransitionHostResource(list, texture, CopyDestination, 0);
                using var ordered = new OrderedTextureUploads(queue);
                ordered.Submit(utility);
                using var consumer = AcquireUtilityContext(0, 0);
                var consumerList = consumer.List;
                TransitionHostResource(consumerList, texture, 0, CopySource);
                foreach (var footprint in footprints)
                {
                    RecordHostTextureTransfer(consumerList, texture, readback, bytes, footprint, false);
                }

                TransitionHostResource(consumerList, texture, CopySource, 0);
                consumer.SubmitAndWait(queue);
                ordered.CompleteAfterFence();
            }
            else
            {
                TransitionHostResource(list, texture, CopyDestination, CopySource);
                foreach (var footprint in footprints)
                {
                    RecordHostTextureTransfer(list, texture, readback, bytes, footprint, false);
                }

                TransitionHostResource(list, texture, CopySource, 0);
                utility.SubmitAndWait(queue);
            }

            var read = new HostRange { End = (nuint)bytes };
            var noWrite = default(HostRange);
            var mapped = (void*)null;
            var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
            var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

            CheckHostResult(map(readback, 0, &read, &mapped), "probe texture readback map");

            try
            {
                if (mapped == null)
                {
                    throw new InvalidOperationException("Probe texture readback mapping is null.");
                }

                var result = new ReadOnlySpan<byte>(mapped, expected.Length);

                foreach (var footprint in footprints)
                {
                    if (!TryHostTextureTransferRange(footprint, bytes, out var rows, out var rowBytes))
                    {
                        throw new InvalidOperationException("Probe texture transfer footprint became invalid.");
                    }

                    for (var row = 0U; row < rows; row++)
                    {
                        var offset = (int)(footprint.Offset + (ulong)row * footprint.RowPitch);
                        var count = (int)rowBytes;

                        if (!result.Slice(offset, count).SequenceEqual(expected.Slice(offset, count)))
                        {
                            throw new InvalidOperationException(
                                $"GPU texture readback differs at subresource {footprint.Subresource}, row {row}.");
                        }
                    }
                }
            }
            finally
            {
                unmap(readback, 0, &noWrite);
            }
        }
        finally
        {
            ReleaseHostObject(readback);
            ReleaseHostObject(texture);
        }
    }
}
