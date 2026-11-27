// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Shaders.Raster;

namespace IraRationis.Graphics.Textures.Models;

internal sealed class IndexedUiUpload(
    IndexedUploadPlan plan, ulong vertex, ulong table,
    PlanarColorTextureBinding[] textures, ConstantDescriptor vertexConstants, ConstantDescriptor pixelConstants,
    ulong samplerHandle, ulong samplerToken, uint samplerKind, nint host, nint target, long generation,
    IndexedUiVariant variant, uint colorFormat, int vertexConstantBytes, int pixelConstantOffset, int vertexOffset)
{
    public readonly IndexedUploadPlan Plan = plan;
    public readonly ulong Vertex = vertex;
    public readonly ulong Table = table;
    public readonly PlanarColorTextureBinding[] Textures = textures;
    public readonly ConstantDescriptor VertexConstants = vertexConstants;
    public readonly ConstantDescriptor PixelConstants = pixelConstants;
    public readonly ulong SamplerHandle = samplerHandle;
    public readonly ulong SamplerToken = samplerToken;
    public readonly uint SamplerKind = samplerKind;
    public readonly nint Host = host;
    public readonly nint Target = target;
    public readonly long Generation = generation;
    public readonly IndexedUiVariant Variant = variant;
    public readonly uint ColorFormat = colorFormat;
    public readonly int VertexConstantBytes = vertexConstantBytes;
    public readonly int PixelConstantOffset = pixelConstantOffset;
    public readonly int VertexOffset = vertexOffset;

    public bool Dither
    {
        get
        {
            return Variant == IndexedUiVariant.Dithered;
        }
    }

    public int Phase;
}
