// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Textures.Models;

internal sealed unsafe class TexturedDrawUpload(
    TextureSource transform,
    TextureSource texture,
    ulong table,
    ulong vertex,
    int vertexBytes,
    int capacity,
    nint host,
    nint textureHost,
    nint target)
{
    public readonly int Capacity = capacity;
    public readonly nint Host = host;
    public readonly ulong Table = table;
    public readonly nint Target = target;
    public readonly TextureSource Texture = texture;
    public readonly nint TextureHost = textureHost;
    public readonly TextureSource Transform = transform;
    public readonly ulong Vertex = vertex;
    public readonly int VertexBytes = vertexBytes;
    public int Phase;
}
