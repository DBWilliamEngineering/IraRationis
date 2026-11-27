// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Drawing;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<nint, PlanarColorPipeline>>
        PlanarColorPipelines = new();

    private static readonly NativePipelineCache<GraphicsPipelineMetadata, PlanarColorPipeline>
        SharedPlanarColorPipelines = new("planar-color", static pipeline => (pipeline.Root, pipeline.Pipeline));

    private static bool IsPlanarColorLayout(VertexInputElement[] elements)
    {
        return elements.Length == 3 && elements.All(static element => element.Index == 0 && element.Slot == 0 &&
                                                                      element.Class == 0 && element.Step == 0
                                                                      && element.Name.ToUpperInvariant() switch
                                                                      {
                                                                          "POSITION" => element.Format == 6
                                                                              && element.Offset == 0,
                                                                          "COLOR" => element.Format == 28
                                                                              && element.Offset == 12,
                                                                          "TEXCOORD" => element.Format == 16
                                                                              && element.Offset == 16,
                                                                          _ => false
                                                                      }) && elements.Select(static element =>
            element.Name.ToUpperInvariant()).Distinct().Count() == 3;
    }

    private static bool IsSrgbTextureStrip(GraphicsPipelineMetadata metadata)
    {
        return metadata.Shaders.Count == 2 && metadata.Shaders.Any(static shader => shader.Stage == 1 &&
            shader.Hash == SrgbTextureStripCompiler.VertexHash) && metadata.Shaders.Any(static shader =>
            shader.Stage == 2 &&
            shader.Hash == SrgbTextureStripCompiler.PixelHash);
    }

    private static PlanarColorPipeline? GetPlanarColorPipeline(RecordedCommandState state,
        GraphicsPipelineMetadata metadata, bool srgbTexture)
    {
        if (metadata.Root != state.Graphics.Signature || !IsRootSignature(metadata.Root) ||
            !metadata.CompleteStream || !metadata.IsStream || metadata.Shaders.Count != 2 ||
            !TryNormalizeRectangleLayout(metadata.InputLayout, 24, out var elements) || !IsPlanarColorLayout(elements))
        {
            _ = RejectPlanarColorDraw("unsupported root signature, pipeline shape, or 24-byte input layout");

            return null;
        }

        var cache = PlanarColorPipelines.GetOrCreateValue(state);

        if (cache.TryGetValue(state.Pipeline, out var cached))
        {
            return cached;
        }

        var vertex = metadata.Shaders.FirstOrDefault(static shader => shader.Stage == 1);
        var pixel = metadata.Shaders.FirstOrDefault(static shader => shader.Stage == 2);

        if (vertex == null || pixel == null)
        {
            return null;
        }

        var hostMetadata = metadata;

        if (srgbTexture && !TextureStripDepthPolicy.TryNormalize(metadata, out hostMetadata))
        {
            _ = RejectPlanarColorDraw("sRGB texture-strip requires disabled depth, writes, stencil and bounds");

            return null;
        }

        if (SharedPlanarColorPipelines.Acquire(metadata, state) is { } shared)
        {
            cache.Add(state.Pipeline, shared);

            return shared;
        }

        void* root = null;
        nint pipeline = 0;

        try
        {
            var guestRoot = RootSignatureBlob(metadata.Root).ToArray();
            var translated = srgbTexture
                ? SrgbTextureStripCompiler.Translate(vertex.Container.Bytes, pixel.Container.Bytes, guestRoot)
                : PlanarColorShaderCompiler.Translate(vertex.Container.Bytes, pixel.Container.Bytes, guestRoot);
            var report = Console.Error;
            var vertexShader = CompileHostShader(translated.VertexSource, "vs_5_1", report);
            var pixelShader = CompileHostShader(translated.PixelSource, "ps_5_1", report);
            var blob = SerializePlanarColorRoot(report);

            if (vertexShader == null || pixelShader == null || blob == null)
            {
                return null;
            }

            var rootId = RootSignatureId;

            fixed (byte* bytes = blob)
            {
                var resultCode = (int)_host->CreateRootSignature(0, bytes, (nuint)blob.Length, &rootId, &root);

                if (resultCode < 0 || root == null)
                {
                    _ = RejectPlanarColorDraw($"host root creation HRESULT=0x{resultCode:X8}");

                    return null;
                }
            }

            pipeline = CreatePlanarColorPipeline(root, vertexShader, pixelShader, hostMetadata, elements, report);

            if (pipeline == 0)
            {
                return null;
            }

            var result = new PlanarColorPipeline((nint)root, pipeline, srgbTexture);

            state.HostViews.Add((nint)root);

            root = null;

            state.HostViews.Add(pipeline);

            pipeline = 0;

            cache.Add(state.Pipeline, result);

            SharedPlanarColorPipelines.Publish(metadata, result);

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                              NotSupportedException or OverflowException or DllNotFoundException
                                              or EntryPointNotFoundException)
        {
            _ = RejectPlanarColorDraw(exception.Message);

            return null;
        }
        finally
        {
            if (pipeline != 0)
            {
                _ = HostRelease(pipeline);
            }

            if (root != null)
            {
                _ = HostRelease((nint)root);
            }
        }
    }

    private static nint CreatePlanarColorPipeline(void* root, byte[] vertexShader, byte[] pixelShader,
        GraphicsPipelineMetadata metadata, VertexInputElement[] elements, TextWriter report)
    {
        var native = stackalloc NativeVertexInputElement[3];

        fixed (byte* position = "POSITION\0"u8)
        fixed (byte* color = "COLOR\0"u8)
        fixed (byte* textureCoordinate = "TEXCOORD\0"u8)
        {
            for (var elementIndex = 0; elementIndex < elements.Length; elementIndex++)
            {
                var element = elements[elementIndex];

                native[elementIndex] = new NativeVertexInputElement
                {
                    Name = element.Name.ToUpperInvariant() switch
                    {
                        "POSITION" => (nint)position,
                        "COLOR" => (nint)color,
                        _ => (nint)textureCoordinate
                    },
                    Index = element.Index,
                    Format = element.Format,
                    Slot = element.Slot,
                    Offset = element.Offset,
                    Class = element.Class,
                    Step = element.Step
                };
            }

            return CreatePipelineCandidate(root, vertexShader, pixelShader, metadata, report,
                null, native, (uint)elements.Length,
                true);
        }
    }
}
