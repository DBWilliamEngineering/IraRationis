/*
 * @file        ColorLutInputProbe.cs
 * @description Verifies GPU upload and readback of a borrowed private color LUT texture.
 * @author      Damon William
 * @date        2026-11-27
 *
 * @details     The probe compares exact RGBA32_FLOAT texel bytes after a host copy.
 *
 * See the repository license and attribution notices.
 */

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static void VerifyColorLutInputUpload(nint queue)
    {
        const int width = 2;
        const int height = 2;
        const int bytesPerPixel = 16;

        var description = RasterTexture(width, height, 0x300004);

        description.Format = ColorLutInputFormat;
        description.Layout = 0x102;

        if (!TryPlanTextureTexture(description, out var plan) || plan is null)
        {
            throw new InvalidOperationException("Color LUT input probe could not plan the tiled texture.");
        }

        ReadOnlySpan<float> texels = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        var expected = MemoryMarshal.AsBytes(texels);
        var backingResource = CreateTransferProbeResource(description);
        nint guest = 0;
        nint readback = 0;

        try
        {
            var iid = ResourceId;
            var address = ((ResourceObject*)backingResource)->Address;

            var properties = new GraphicsHeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };

            CheckHostResult(NewResource(description, properties, 0, address, 0, 0, &iid, &guest),
                "borrowed color LUT input resource");

            var resource = (ResourceObject*)guest;
            var source = new TextureSource(0, 0, 18, guest, resource->Generation, null);

            if (!TryGetTextureView(source, out var inputFormat) || !IsCpuBackedColorLutInput(source, inputFormat) ||
                IsCpuBackedColorLutInput(source, 28))
            {
                throw new InvalidOperationException(
                    "Color LUT input probe did not enforce the RGBA32_FLOAT upload contract.");
            }

            var plane = plan.Layout.Planes[0];
            var mip = plane.Mips[0];
            var patterns = plane.Patterns();
            var backing = new Span<byte>((void*)(nuint)address, checked((int)plan.Layout.Size));

            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var tiledX = (uint)column + mip.TailX;
                    var tiledY = (uint)row + mip.TailY;

                    var tile = (ulong)(tiledY / plane.TileHeight) * (mip.Pitch / plane.TileWidth)
                               + tiledX / plane.TileWidth;
                    var withinTile = patterns[0][tiledX % plane.TileWidth] |
                                     patterns[1][tiledY % plane.TileHeight] | patterns[2][0];
                    var offset = checked((int)(plane.Offset + mip.Offset + tile * plane.Alignment + withinTile));

                    expected.Slice((row * width + column) * bytesPerPixel, bytesPerPixel).CopyTo(backing[offset..]);
                }
            }

            CheckHostResult(MaterializeHostResource(guest, out var texture), "color LUT input texture");
            RequireHostObject(texture, "color LUT input texture");

            if (!UploadTextureTexture(queue, guest, texture, allowUnorderedAccess: true))
            {
                throw new InvalidOperationException("Color LUT input probe upload failed.");
            }

            CheckHostResult(CreateHostResource(HostBufferDescription(plan.Bytes), 3, CopyDestination, &readback),
                "color LUT input readback");
            RequireHostObject(readback, "color LUT input readback");

            using var copy = AcquireUtilityContext(0, 0);

            TransitionHostResource(copy.List, texture, 0, CopySource);

            var footprint = new HostTextureTransferFootprint(0, ColorLutInputFormat, width, height, plan.Pitches[0], 0);

            RecordHostTextureTransfer(copy.List, texture, readback, plan.Bytes, footprint, false);
            TransitionHostResource(copy.List, texture, CopySource, 0);

            copy.SubmitAndWait(queue);

            var pixels = ReadRasterPixels(readback, width, height, checked((int)plan.Pitches[0]), bytesPerPixel);

            if (!pixels.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException("Color LUT input probe readback differs from the authored texels.");
            }

            Console.WriteLine("[graphics-probe] Borrowed private color LUT input GPU readback passed.");
        }
        finally
        {
            ReleaseHostObject(readback);
            ReleaseProbe(ref guest);
            ReleaseProbe(ref backingResource);
        }
    }
}
