// SPDX-License-Identifier: GPL-3.0-or-later
// Independent linear copy implementation using documented Windows D3D12 contracts.

using System.Buffers;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Textures;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const ulong MaximumLinearCopyBytes = 64UL << 20;
    private const uint MaximumLinearCopyExtent = 16384;
    private static readonly bool LinearTextureTransferEnabled =
        Environment.GetEnvironmentVariable("IRX_EXPERIMENTAL_LINEAR_TEXTURE_COPY") == "1";

    // Opt-in for authorized, authored callers using the documented PC copy-location ABI.
    // A console-specific encoding must not be inferred from a matching method name.
    private static bool TryRecordLinearTextureTransfer(nint self, nint destination, uint coordinateX, uint coordinateY, uint coordinateZ,
        nint source, nint box)
    {
        if (coordinateZ != 0 || !TryReadLinearCopyLocation(destination, out var to) ||
            !TryReadLinearCopyLocation(source, out var from))
        {
            return false;
        }

        var region = default(LinearCopyBox);

        if (box != 0 && !TryReadLinearCopyBox(box, out region))
        {
            return false;
        }

        return TryRecordLinearTransfer(self, from, to, region, coordinateX, coordinateY, box == 0, false);
    }

    private static bool TryRecordWholeLinearTexture(nint self, nint destination, nint source)
    {
        var from = new LinearCopyLocation { Resource = source };
        var destinationPath = new LinearCopyLocation { Resource = destination };

        return TryRecordLinearTransfer(self, from, destinationPath, default, 0, 0, true, true);
    }

    private static bool TryRecordLinearTransfer(nint self, LinearCopyLocation from, LinearCopyLocation destination,
        LinearCopyBox region, uint coordinateX, uint coordinateY, bool fullSource, bool wholeResource)
    {
        var state = StateFor(self);

        if (state.MemoryBoundaries.Count >= MaximumMemoryOperations)
        {
            return false;
        }

        LinearTextureTransfer transfer;

        // Prevent materialization of the selected resources while resolving metadata.
        // Recording threads still require normal external command-list synchronization.
        lock (HostResourceGate)
        {
            if (!TryResolveLinearSurface(from, out var sourceSurface) ||
                !TryResolveLinearSurface(destination, out var destinationSurface) ||
                (from.Kind == 1 && destination.Kind == 1) || from.Resource == destination.Resource)
            {
                return false;
            }

            if (wholeResource && (sourceSurface.Width != destinationSurface.Width ||
                                  sourceSurface.Height != destinationSurface.Height))
            {
                return false;
            }

            if (fullSource)
            {
                region = new LinearCopyBox
                {
                    Right = sourceSurface.Width,
                    Bottom = sourceSurface.Height,
                    Back = 1
                };
            }

            if (!TryPlanLinearCopy(sourceSurface, destinationSurface, region, coordinateX, coordinateY, out var plan))
            {
                return false;
            }

            state.RetainObject(from.Resource);
            state.RetainObject(destination.Resource);

            transfer = new LinearTextureTransfer(from, destination, sourceSurface, destinationSurface, region, coordinateX, coordinateY, plan);
        }

        // Capture the descriptors at recording time, not the pixel bytes. The copy
        // executes after the preceding native segment and its completion fence.
        SealMemoryBoundary(self, state, null, null, new DeferredCopy(0, 0, 0, transfer));

        return true;
    }

    private static bool TryReadLinearCopyLocation(nint address, out LinearCopyLocation location)
    {
        var value = default(LinearCopyLocation);
        nuint transferred = 0;

        var success = address != 0 &&
                      ReadProcessMemory(-1, (void*)address, &value, (nuint)sizeof(LinearCopyLocation), &transferred) != 0 &&
                      transferred == (nuint)sizeof(LinearCopyLocation);

        location = value;

        return success && value.Kind <= 1;
    }

    private static bool TryReadLinearCopyBox(nint address, out LinearCopyBox box)
    {
        var value = default(LinearCopyBox);
        nuint transferred = 0;

        var success = ReadProcessMemory(-1, (void*)address, &value, (nuint)sizeof(LinearCopyBox), &transferred) != 0 &&
                      transferred == (nuint)sizeof(LinearCopyBox);

        box = value;

        return success;
    }

    private static bool TryResolveLinearSurface(LinearCopyLocation location, out LinearSurface surface)
    {
        surface = default;

        if (!IsResource(location.Resource))
        {
            return false;
        }

        var resource = (ResourceObject*)location.Resource;
        var description = resource->Description;

        // No external allocator, placed-resource alias, raw address view, host
        // backing, or GPU-writable resource is eligible for this first slice.
        if (resource->OwnedMemory == 0 || resource->FreeHook != 0 || resource->OwningHeap != 0 ||
            resource->Address != (ulong)(nuint)resource->OwnedMemory || resource->HostResource != 0 ||
            description.Flags != 0 || resource->Address == 0 || resource->Address > (ulong)nint.MaxValue)
        {
            return false;
        }

        if (location.Kind == 1)
        {
            if (description.Dimension != 1 || location.Depth != 1 || location.Offset % 512 != 0 ||
                location.Pitch % 256 != 0 || !TryLinearPixelBytes(location.Format, out var bytes))
            {
                return false;
            }

            return TryCreateLinearSurface(resource->Address, description.Width, resource->Generation, location.Offset,
                location.Width, location.Height, location.Format, bytes, location.Pitch, out surface);
        }

        if (location.Kind != 0 || (uint)location.Offset != 0 || description.Dimension != 3 ||
            description.Layout != 1 || description.SampleCount != 1 || description.MipLevels != 1 ||
            description.DepthOrArraySize != 1 || !TryLinearPixelBytes(description.Format, out var pixelBytes) ||
            !TextureFootprintCalculator.TryCreate(description, out var layout) || layout.Planes.Length != 1)
        {
            return false;
        }

        var plane = layout.Planes[0];

        if (plane.Mode != 0 || plane.Bytes != pixelBytes || plane.Mips.Length != 1 || plane.Slices != 1)
        {
            return false;
        }

        var mip = plane.Mips[0];

        return TryCreateLinearSurface(resource->Address, layout.Size, resource->Generation, plane.Offset + mip.Offset,
            mip.Width, mip.Height, description.Format, pixelBytes, (ulong)mip.Pitch * pixelBytes, out surface);
    }

    private static bool TryLinearPixelBytes(uint format, out uint bytes)
    {
        // Only exact, typed, single-plane uncompressed color formats. No typeless,
        // compressed, depth/stencil, planar, packed or reinterpretation support.
        bytes = format switch
        {
            2 => 16,
            10 or 11 or 16 => 8,
            28 or 29 or 34 or 35 or 41 or 42 or 87 or 91 => 4,
            49 or 54 or 56 => 2,
            61 or 62 => 1,
            _ => 0
        };

        return bytes != 0;
    }

    private static bool TryCreateLinearSurface(ulong address, ulong capacity, long generation, ulong offset,
        uint width, uint height, uint format, uint pixelBytes, ulong pitch, out LinearSurface surface)
    {
        surface = default;

        if (width is 0 or > MaximumLinearCopyExtent || height is 0 or > MaximumLinearCopyExtent ||
            pixelBytes == 0 || address == 0 || address > (ulong)nint.MaxValue ||
            capacity > (ulong)nint.MaxValue - address || offset > capacity || pitch > capacity)
        {
            return false;
        }

        var rowBytes = (ulong)width * pixelBytes;

        if (pitch < rowBytes || rowBytes > capacity - offset ||
            (ulong)(height - 1) > (capacity - offset - rowBytes) / pitch)
        {
            return false;
        }

        surface = new LinearSurface(address + offset, width, height, format, pixelBytes, pitch, generation);

        return true;
    }

    private static bool TryPlanLinearCopy(LinearSurface source, LinearSurface destination, LinearCopyBox region,
        uint coordinateX, uint coordinateY, out LinearCopyPlan plan)
    {
        plan = default;

        if (source.Format != destination.Format || source.PixelBytes != destination.PixelBytes ||
            region.Front != 0 || region.Back != 1 || region.Left >= region.Right || region.Top >= region.Bottom ||
            region.Right > source.Width || region.Bottom > source.Height || coordinateX > destination.Width ||
            coordinateY > destination.Height)
        {
            return false;
        }

        var width = region.Right - region.Left;
        var height = region.Bottom - region.Top;
        var rowBytes = (ulong)width * source.PixelBytes;

        if (width > destination.Width - coordinateX || height > destination.Height - coordinateY ||
            rowBytes == 0 || rowBytes > MaximumLinearCopyBytes / height)
        {
            return false;
        }

        var from = source.Address + region.Top * source.Pitch + (ulong)region.Left * source.PixelBytes;
        var destinationPath = destination.Address + coordinateY * destination.Pitch + (ulong)coordinateX * destination.PixelBytes;

        plan = new LinearCopyPlan(from, destinationPath, source.Pitch, destination.Pitch, (int)rowBytes, (int)height);

        return true;
    }

    private static void ExecuteLinearTextureTransfer(LinearTextureTransfer transfer)
    {
        lock (HostResourceGate)
        {
            // A list may be replayed. Re-resolve its retained resources every time;
            // never reuse a CPU copy plan if host backing or identity has changed.
            if (!TryResolveLinearSurface(transfer.Source, out var source) || source != transfer.SourceSurface ||
                !TryResolveLinearSurface(transfer.Destination, out var destination) ||
                destination != transfer.DestinationSurface ||
                !TryPlanLinearCopy(source, destination, transfer.Region, transfer.X, transfer.Y, out var plan) ||
                plan != transfer.Plan)
            {
                Environment.FailFast("[graphics] R55 linear transfer resource identity or CPU ownership changed.");
            }

            CopyLinearRows(transfer.Plan);
        }

        TraceGraphicsSuccess($"[graphics] IRX-R55-LINEAR-COPY: rows={transfer.Plan.Rows}; " +
                 $"rowBytes={transfer.Plan.RowBytes}; bytes={(long)transfer.Plan.Rows * transfer.Plan.RowBytes}; CPU-only.");
    }

    private static void CopyLinearRows(LinearCopyPlan plan)
    {
        var size = checked(plan.Rows * plan.RowBytes);
        var scratch = ArrayPool<byte>.Shared.Rent(size);

        try
        {
            fixed (byte* bytes = scratch)
            {
                // Snapshot every source row before writing any destination row.
                // A failed source read cannot leave a partially updated texture.
                for (var row = 0; row < plan.Rows; row++)
                {
                    var address = plan.Source + (ulong)row * plan.SourcePitch;
                    nuint transferred = 0;

                    if (ReadProcessMemory(-1, (void*)(nuint)address, bytes + row * plan.RowBytes,
                            (nuint)plan.RowBytes, &transferred) == 0 || transferred != (nuint)plan.RowBytes)
                    {
                        Environment.FailFast("[graphics] R55 linear copy source is unreadable.");
                    }
                }

                for (var row = 0; row < plan.Rows; row++)
                {
                    var address = plan.Destination + (ulong)row * plan.DestinationPitch;
                    nuint transferred = 0;

                    if (WriteProcessMemory(-1, (void*)(nuint)address, bytes + row * plan.RowBytes,
                            (nuint)plan.RowBytes, &transferred) == 0 || transferred != (nuint)plan.RowBytes)
                    {
                        Environment.FailFast("[graphics] R55 linear copy destination is unwritable.");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch, clearArray: true);
        }
    }

    // Existing resource ABI probe: authored CPU-owned resources, actual recorded
    // boundaries, no production account or third-party content dependency.
    private static void VerifyLinearTextureTransfer()
    {
        if (sizeof(LinearCopyLocation) != 48 || sizeof(LinearCopyBox) != 24)
        {
            throw new InvalidOperationException("R55 PC copy ABI size mismatch.");
        }

        var textureDescription = new ResourceDescription
        {
            Dimension = 3,
            Width = 6,
            Height = 4,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 28,
            SampleCount = 1,
            Layout = 1
        };
        var buffer = nint.Zero;
        var texture = nint.Zero;
        var duplicate = nint.Zero;
        var list = nint.Zero;

        try
        {
            buffer = CreateLinearProbeResource(HostBufferDescription(2048));
            texture = CreateLinearProbeResource(textureDescription);
            duplicate = CreateLinearProbeResource(textureDescription);
            list = (nint)NativeMemory.AllocZeroed(0x980);

            if (list == 0)
            {
                throw new OutOfMemoryException("R55 fixture list allocation failed.");
            }

            ((GraphicsObjectHeader*)list)->Table = _listNew;

            LiveObjects[list] = _listNew;

            var from = new LinearCopyLocation
            {
                Resource = buffer,
                Kind = 1,
                Format = 28,
                Width = 5,
                Height = 4,
                Depth = 1,
                Pitch = 256
            };
            var destination = from;

            destination.Offset = 1024;
            destination.Width = 6;

            var firstTexture = new LinearCopyLocation { Resource = texture };
            var secondTexture = new LinearCopyLocation { Resource = duplicate };
            var region = new LinearCopyBox { Left = 1, Top = 1, Right = 4, Bottom = 3, Back = 1 };
            var bytes = new Span<byte>((void*)(nuint)((ResourceObject*)buffer)->Address, 2048);

            bytes.Fill(0xCD);

            for (var index = 0; index < 1024; index++)
            {
                bytes[index] = (byte)(index * 17 + index / 256);
            }

            if (!TryResolveLinearSurface(firstTexture, out var surface) || surface.Pitch != 256 ||
                !TryResolveLinearSurface(secondTexture, out var secondSurface) || secondSurface.Pitch != 256)
            {
                throw new InvalidOperationException("R55 fixture surface resolution failed.");
            }

            var pixels = new Span<byte>((void*)(nuint)surface.Address, 1024);
            var duplicatePixels = new Span<byte>((void*)(nuint)secondSurface.Address, 1024);

            pixels.Fill(0xCE);
            duplicatePixels.Fill(0xCF);

            // Invalid requests cannot add a boundary or touch the destination.
            var invalid = from;

            invalid.Pitch = 128;

            if (TryRecordLinearTextureTransfer(list, (nint)(&firstTexture), 4, 0, 0, (nint)(&from),
                    (nint)(&region)) ||
                TryRecordLinearTextureTransfer(list, (nint)(&firstTexture), 2, 0, 0, (nint)(&invalid),
                    (nint)(&region)))
            {
                throw new InvalidOperationException("R55 invalid copy was accepted.");
            }

            if (!TryRecordLinearTextureTransfer(list, (nint)(&firstTexture), 2, 0, 0, (nint)(&from),
                    (nint)(&region)) ||
                !TryRecordWholeLinearTexture(list, duplicate, texture) ||
                !TryRecordLinearTextureTransfer(list, (nint)(&destination), 0, 0, 0, (nint)(&secondTexture), 0))
            {
                throw new InvalidOperationException("R55 valid transfer was rejected.");
            }

            var state = StateFor(list);

            if (state.MemoryBoundaries.Count != 3 || state.Owned.Count != 3 ||
                pixels[8] != 0xCE || duplicatePixels[8] != 0xCF || bytes[1032] != 0xCD)
            {
                throw new InvalidOperationException("R55 transfer executed before submission or lost ownership.");
            }

            // The caller releases its references before execution. The recorded
            // state must keep the three resources alive until list retirement.
            var sourceIdentity = buffer;
            var firstIdentity = texture;
            var secondIdentity = duplicate;

            ReleaseProbe(ref buffer);
            ReleaseProbe(ref texture);
            ReleaseProbe(ref duplicate);

            if (!IsResource(sourceIdentity) || !IsResource(firstIdentity) || !IsResource(secondIdentity))
            {
                throw new InvalidOperationException("R55 retained resource disappeared before execution.");
            }

            bytes[260] = 0x42;

            foreach (var boundary in state.MemoryBoundaries)
            {
                if (boundary.Copy is not { } copy)
                {
                    throw new InvalidOperationException("R55 transfer boundary missing.");
                }

                ExecuteDeferredCopy(copy);
            }

            for (var index = 0; index < 1024; index++)
            {
                var row = index / 256;
                var column = index % 256;
                var expected = row < 2 && column is >= 8 and < 20
                    ? bytes[(row + 1) * 256 + column - 4]
                    : (byte)0xCE;
                var expectedDuplicate = column < 24 ? expected : (byte)0xCF;
                var expectedBuffer = column < 24 ? expected : (byte)0xCD;

                if (pixels[index] != expected || duplicatePixels[index] != expectedDuplicate ||
                    bytes[1024 + index] != expectedBuffer)
                {
                    throw new InvalidOperationException("R55 copy, deferred source capture or padding mismatch.");
                }
            }

            ReleaseCommandState(list);

            if (IsResource(sourceIdentity) || IsResource(firstIdentity) || IsResource(secondIdentity))
            {
                throw new InvalidOperationException("R55 recorded resource references were not retired.");
            }
        }
        finally
        {
            if (list != 0)
            {
                ReleaseCommandState(list);

                _ = LiveObjects.TryRemove(list, out _);

                NativeMemory.Free((void*)list);
            }

            ReleaseProbe(ref duplicate);
            ReleaseProbe(ref texture);
            ReleaseProbe(ref buffer);
        }

        Console.WriteLine("[graphics-probe] IRX-R55: region upload, whole texture copy, buffer readback, " +
                          "bounds, padding, deferred ordering and resource retirement verified (CPU only).");
    }

    private static nint CreateLinearProbeResource(ResourceDescription description)
    {
        if (!ResourceFootprint(description, out var footprint))
        {
            throw new InvalidOperationException("R55 fixture footprint unavailable.");
        }

        var memory = AllocateBacking(footprint.Size, footprint.Alignment);

        if (memory == 0)
        {
            throw new OutOfMemoryException("R55 fixture backing unavailable.");
        }

        var resource = nint.Zero;
        var id = ResourceId;
        var properties = new GraphicsHeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };

        // NewResource owns the backing, including on its error paths.
        CheckHostResult(NewResource(description, properties, 0, (ulong)(nuint)memory, 0, memory, &id, &resource),
            "R55 fixture resource");

        return resource;
    }
}
