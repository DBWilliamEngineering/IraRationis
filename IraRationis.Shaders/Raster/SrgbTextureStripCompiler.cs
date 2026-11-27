// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using IraRationis.Shaders.Raster.Models;

namespace IraRationis.Shaders.Raster;

// Bounded lowering of the independently decoded two-transform, one-texture,
// RGB transfer and half-export contract. Unknown containers must not use it.
public static class SrgbTextureStripCompiler
{
    public const string VertexHash = "B2676E32C2A48EB8B026D67FA8F70AA5055D24F88804A359A42F53429114A541";
    public const string PixelHash = "A7CC0550C706BBE670F5FA2F728750642362825294204EE474F83986798CAD63";
    private const string RootHash = "14619793F5B26A039D76CACB5219BC488036B5A4E3166263D700BEFDBF605018";

    public static bool Matches(string vertex, string pixel)
    {
        return vertex == VertexHash && pixel == PixelHash;
    }

    public static PlanarColorShaderPair Translate(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> pixel,
        ReadOnlyMemory<byte> root)
    {
        if (!Matches(Convert.ToHexString(SHA256.HashData(vertex.Span)),
                Convert.ToHexString(SHA256.HashData(pixel.Span))) ||
            Convert.ToHexString(SHA256.HashData(root.Span)) != RootHash)
        {
            throw new NotSupportedException("Unsupported sRGB texture-strip shader or root contract.");
        }

        return Rdna2RectangleCompiler.BuildSrgbTextureStrip();
    }

    public static PlanarColorShaderPair VerifyAuthoredContracts()
    {
        if (!Matches(VertexHash, PixelHash) || Matches(PixelHash, VertexHash) ||
            Matches(VertexHash, string.Empty) || Matches(string.Empty, PixelHash))
        {
            throw new InvalidOperationException("Texture-strip shader classification failed.");
        }

        try
        {
            _ = Translate(new byte[16], new byte[16], new byte[16]);
            throw new InvalidOperationException("Unknown texture-strip shaders were accepted.");
        }
        catch (NotSupportedException)
        {
        }

        return Rdna2RectangleCompiler.BuildSrgbTextureStrip();
    }
}
