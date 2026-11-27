// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Shaders.Raster;

namespace IraRationis.Graphics.Textures.Models;

internal sealed class IndexedUiUpload(
    IndexedUploadPlan plan,
    ulong vertex,
    ulong table,
    PlanarColorTextureBinding[] textures,
    ConstantDescriptor vertexConstants,
    ConstantDescriptor pixelConstants,
    ulong samplerHandle,
    ulong samplerToken,
    uint samplerKind,
    nint host,
    nint target,
    long generation,
    IndexedUiVariant variant,
    uint colorFormat,
    int vertexConstantBytes,
    int pixelConstantOffset,
    int vertexOffset)
{
    public readonly uint ColorFormat = colorFormat;
    public readonly long Generation = generation;
    public readonly nint Host = host;
    public readonly int PixelConstantOffset = pixelConstantOffset;
    public readonly ConstantDescriptor PixelConstants = pixelConstants;
    public readonly IndexedUploadPlan Plan = plan;
    public readonly ulong SamplerHandle = samplerHandle;
    public readonly uint SamplerKind = samplerKind;
    public readonly ulong SamplerToken = samplerToken;
    public readonly ulong Table = table;
    public readonly nint Target = target;
    public readonly PlanarColorTextureBinding[] Textures = textures;
    public readonly IndexedUiVariant Variant = variant;
    public readonly ulong Vertex = vertex;
    public readonly int VertexConstantBytes = vertexConstantBytes;
    public readonly ConstantDescriptor VertexConstants = vertexConstants;
    public readonly int VertexOffset = vertexOffset;

    public int Phase;

    public bool Dither
    {
        get
        {
            return Variant == IndexedUiVariant.Dithered;
        }
    }
}
