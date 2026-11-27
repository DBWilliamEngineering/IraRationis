// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Textures.Models;

internal sealed class PlanarColorDrawUpload(
    TextureSource transform,
    PlanarColorTextureBinding[] textures,
    ulong table,
    ulong vertex,
    int vertexBytes,
    int capacity,
    nint host,
    nint target,
    long targetGeneration,
    bool srgbTexture)
{
    public readonly TextureSource Transform = transform;
    public readonly PlanarColorTextureBinding[] Textures = textures;
    public readonly ulong Table = table;
    public readonly ulong Vertex = vertex;
    public readonly int VertexBytes = vertexBytes;
    public readonly int Capacity = capacity;
    public readonly nint Host = host;
    public readonly nint Target = target;
    public readonly long TargetGeneration = targetGeneration;
    public readonly bool SrgbTexture = srgbTexture;
    public int Phase;
}
