// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static void ExecutePlanarColorDrawBoundary(nint queue, PlanarColorDrawBoundary boundary,
        OrderedTextureUploads? ordered = null)
    {
        using var stageTiming = BeginTiming(GraphicsStage.ExecuteTexture);

        var upload = boundary.Upload;

        if (boundary.Complete)
        {
            CompletePlanarColorDraw(queue, upload);

            return;
        }

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0)
        {
            Environment.FailFast("[graphics] planar-color command list requires reset before resubmission.");
        }

        var lookupEnabled = upload.SrgbTexture
            ? UploadSrgbTextureStripConstants(upload)
            : UploadPlanarColorConstants(upload);

        PreparePlanarColorTextures(queue, upload.Textures, lookupEnabled, ordered);

        if (upload.SrgbTexture)
        {
            TraceGraphicsSuccess(
                $"[graphics] srgb-texture-strip PREPARED: vertexBytes={upload.VertexBytes}; transformBytes=64+64.");
        }
        else
        {
            TraceGraphicsSuccess($"[graphics] planar-color PREPARED: vertexBytes={upload.VertexBytes}; " +
                                 $"transformBytes=64+64; sharedBytes={PlanarColorSharedConstantBytes}; lookupEnabled={lookupEnabled}.");
        }
    }

    private static void CompletePlanarColorDraw(nint queue, PlanarColorDrawUpload upload)
    {
        if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
        {
            Environment.FailFast("[graphics] planar-color completion without preparation.");
        }

        MarkGpuWrite(upload.Target, queue);

        Interlocked.Increment(ref _draws);

        TraceGraphicsSuccess(upload.SrgbTexture
            ? "[graphics] srgb-texture-strip GPU COMPLETED."
            : "[graphics] planar-color GPU COMPLETED triangle strip.");

        TraceColor(queue, upload.Target, upload.SrgbTexture ? "srgb-texture-strip-output" : "planar-color-output");

        PreviewCompletedTextureDraw(queue, upload.Target);
    }

    private static bool ArePlanarColorSourcesCurrent(PlanarColorDrawUpload upload)
    {
        return IsResource(upload.Target) && ((ResourceObject*)upload.Target)->Generation == upload.TargetGeneration &&
               IsTextureSourceCurrent(upload.Transform) &&
               upload.Textures.All(static binding => IsTextureSourceCurrent(binding.Source));
    }

    private static bool UploadSrgbTextureStripConstants(PlanarColorDrawUpload upload)
    {
        ulong transformAddress = 0;
        ConstantDescriptor clipConstants = default;

        if (upload.Textures.Length != 1 || !ArePlanarColorSourcesCurrent(upload) ||
            !TryGetTextureBuffer(upload.Transform, out transformAddress) ||
            !TryReadConstantDescriptor(upload.Table + 0x40, TextureSecondTransformOffset + TextureTransformBytes,
                out clipConstants))
        {
            Environment.FailFast("[graphics] srgb-texture-strip sources or clip-transform descriptor invalid.");
        }

        var rented = ArrayPool<byte>.Shared.Rent(upload.Capacity);

        try
        {
            var snapshot = rented.AsSpan(0, upload.Capacity);
            snapshot.Clear();

            if (!TryReadGuestMemory(transformAddress, snapshot[..TextureTransformBytes]) ||
                !TryReadGuestMemory(clipConstants.Address + TextureSecondTransformOffset,
                    snapshot.Slice(256, TextureTransformBytes)) ||
                !TryReadGuestMemory(upload.Vertex, snapshot.Slice(PlanarColorVertexOffset, upload.VertexBytes)) ||
                !TryReadMappingRecord(upload.Table + 0x40, out ConstantDescriptor recheck) ||
                !MatchesConstantDescriptor(clipConstants, recheck) || !ArePlanarColorSourcesCurrent(upload))
            {
                Environment.FailFast("[graphics] srgb-texture-strip submission snapshot changed or is unreadable.");
            }

            MapPlanarColorUpload(upload.Host, snapshot);

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool UploadPlanarColorConstants(PlanarColorDrawUpload upload)
    {
        ulong transformAddress = 0;
        ConstantDescriptor transformConstants = default;
        ConstantDescriptor sharedConstants = default;

        if (!ArePlanarColorSourcesCurrent(upload) || !TryGetTextureBuffer(upload.Transform, out transformAddress) ||
            !TryReadConstantDescriptor(upload.Table + 0xA0, TextureSecondTransformOffset + TextureTransformBytes,
                out transformConstants) ||
            !TryReadConstantDescriptor(upload.Table + 0xC0, PlanarColorSharedConstantBytes, out sharedConstants))
        {
            Environment.FailFast(
                "[graphics] planar-color sources changed or constant descriptors lack their full required extent.");
        }

        var rented = ArrayPool<byte>.Shared.Rent(upload.Capacity);

        try
        {
            var snapshot = rented.AsSpan(0, upload.Capacity);

            snapshot.Clear();

            if (!TryReadGuestMemory(transformAddress, snapshot[..TextureTransformBytes]) ||
                !TryReadGuestMemory(transformConstants.Address + TextureSecondTransformOffset,
                    snapshot.Slice(256, TextureTransformBytes)) ||
                !TryReadGuestMemory(sharedConstants.Address, snapshot.Slice(512, PlanarColorSharedConstantBytes)) ||
                !TryReadGuestMemory(upload.Vertex, snapshot.Slice(PlanarColorVertexOffset, upload.VertexBytes)) ||
                !TryReadMappingRecord(upload.Table + 0xA0, out ConstantDescriptor transformRecheck) ||
                !TryReadMappingRecord(upload.Table + 0xC0, out ConstantDescriptor sharedRecheck) ||
                !MatchesConstantDescriptor(transformConstants, transformRecheck) ||
                !MatchesConstantDescriptor(sharedConstants, sharedRecheck) || !ArePlanarColorSourcesCurrent(upload))
            {
                Environment.FailFast(
                    "[graphics] planar-color submission snapshot is unreadable or its descriptors changed.");
            }

            var lookupEnabled = ReadDescriptorUInt32(snapshot, 512 + PlanarColorLookupEnableOffset) != 0;

            if (lookupEnabled && upload.Textures.Length != 4)
            {
                Environment.FailFast(
                    "[graphics] planar-color lookup is enabled but table+0x80 lacks a supported retained Texture3D SRV.");
            }

            MapPlanarColorUpload(upload.Host, snapshot);

            return lookupEnabled;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void MapPlanarColorUpload(nint host, ReadOnlySpan<byte> snapshot)
    {
        var noRead = default(HostRange);

        void* mapped = null;

        CheckHostResult(((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(host, 8))
            (host, 0, &noRead, &mapped), "map planar-color draw upload");

        if (mapped == null)
        {
            Environment.FailFast("[graphics] planar-color upload mapping returned null.");
        }

        var written = new HostRange { End = (nuint)snapshot.Length };

        try
        {
            snapshot.CopyTo(new Span<byte>(mapped, snapshot.Length));
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(host, 9))(host, 0, &written);
        }
    }

    private static void PreparePlanarColorTextures(nint queue, PlanarColorTextureBinding[] textures, bool lookupEnabled,
        OrderedTextureUploads? ordered = null)
    {
        for (var textureIndex = 0; textureIndex < textures.Length; textureIndex++)
        {
            var binding = textures[textureIndex];
            var writer = GetResourceWriterQueue(binding.Source.Resource);

            if (writer != 0 && writer != queue)
            {
                Environment.FailFast(
                    $"[graphics] planar-color texture {textureIndex} was written on another queue; coherence is not established.");
            }

            if (binding.DepthPlaneSubresource.HasValue)
            {
                if (writer == 0)
                {
                    Environment.FailFast(
                        $"[graphics] depth plane descriptor={binding.Source.Token} requires an earlier GPU write on its queue.");
                }

                continue;
            }

            if (textureIndex == 3)
            {
                if (lookupEnabled && writer == 0)
                {
                    Environment.FailFast(
                        "[graphics] planar-color Texture3D lookup requires a completed prior same-queue GPU writer; CPU volume upload is unsupported.");
                }

                continue;
            }

            if (writer == 0 && !HasEarlierPlanarColorHost(textures, textureIndex) &&
                !UploadTextureTexture(queue, binding.Source.Resource, binding.Host, ordered))
            {
                Environment.FailFast(
                    $"[graphics] planar-color plane {textureIndex} upload refused; inspect its layout or source diagnostic.");
            }
        }
    }
}
