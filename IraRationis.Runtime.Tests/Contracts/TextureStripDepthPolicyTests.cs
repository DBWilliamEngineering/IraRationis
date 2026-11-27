// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using IraRationis.Graphics;
using IraRationis.Graphics.Shaders.Models;

namespace IraRationis.Runtime.Tests.Contracts;

internal static class TextureStripDepthPolicyTests
{
    internal static void Run()
    {
        var depth = new byte[64];
        var format = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(depth, 21);
        BinaryPrimitives.WriteUInt32LittleEndian(format, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), 20);
        var metadata = new GraphicsPipelineMetadata(0, [],
            [new PipelineStateRecord(21, depth), new PipelineStateRecord(16, format)], true, true);

        if (!TextureStripDepthPolicy.TryNormalize(metadata, out var normalized) ||
            BinaryPrimitives.ReadUInt32LittleEndian(normalized.State[1].Bytes.AsSpan(4)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4)) != 20 ||
            ReferenceEquals(metadata.State, normalized.State))
        {
            throw new InvalidOperationException("Inactive typed DSV normalization failed or mutated the guest PSO.");
        }

        foreach (var offset in new[] { 4, 8, 16, 56 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(offset), 1);

            if (TextureStripDepthPolicy.TryNormalize(metadata, out _))
            {
                throw new InvalidOperationException($"Active depth/stencil state at {offset} accepted.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(offset), 0);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), 45);

        if (TextureStripDepthPolicy.TryNormalize(metadata, out _) ||
            TextureStripDepthPolicy.TryNormalize(metadata with { State = [new PipelineStateRecord(21, depth)] }, out _)
            ||
            TextureStripDepthPolicy.TryNormalize(
                metadata with
                {
                    State = [new PipelineStateRecord(21, depth), new PipelineStateRecord(16, new byte[4])]
                }, out _))
        {
            throw new InvalidOperationException("Unsupported DSV format or malformed depth state accepted.");
        }

        Console.WriteLine(
            "sRGB texture-strip inactive DSV normalization and active-depth rejection passed (CPU only).");
    }
}
