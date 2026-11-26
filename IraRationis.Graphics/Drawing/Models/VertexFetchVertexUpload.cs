// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Shaders.VertexFetch.Models;

namespace IraRationis.Graphics.Drawing.Models;

internal sealed unsafe class VertexFetchVertexUpload(VertexFetchShaderPair pair, ulong address, uint first, uint count, nint host)
{
    public readonly ulong Address = address;
    public readonly uint Count = count;
    public readonly uint First = first;
    public readonly nint Host = host;
    public readonly VertexFetchShaderPair Pair = pair;
    public int Phase;
    public nint Target;
}
