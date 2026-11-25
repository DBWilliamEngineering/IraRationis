// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MaximumComputeShaderBytes = 64 * 1024 * 1024;
    private const long MaximumRetainedComputeBytes = 512L * 1024 * 1024;
    private static readonly Guid PipelineId = new("765a30f3-f624-4c6f-a828-ace948622445");
    private static readonly object PipelineGate = new();
    private static readonly Dictionary<string, ShaderContainer> ComputeShaders = new();
    private static readonly Dictionary<nint, ComputePipelineState> ComputePipelines = new();
    private static long _retainedComputeBytes;
    private static long _computeCreated;
    private static nint _pipelineTable;

    private static bool IsPipeline(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _pipelineTable;
    }

    private static void RegisterComputePipelineMethods(nint* device)
    {
        if (sizeof(ComputeDescription) != 24 || sizeof(PipelineObject) != 48)
        {
            throw new InvalidOperationException("Unexpected compute pipeline ABI layout.");
        }

        _pipelineTable = (nint)ChildTable(24);
        device[11] =
            (nint)(delegate* unmanaged[Stdcall]<nint, ComputeDescription*, Guid*, nint*, int>)&CreateComputePipeline;
        device[62] =
            (nint)(delegate* unmanaged[Stdcall]<nint, ComputeDescription*, uint, void*, Guid*, nint*, int>)
            &CreateComputePipelineX;
    }

    private static void ValidateComputePipelineRegistration(nint* device)
    {
        if (device[11]
            != (nint)(delegate* unmanaged[Stdcall]<nint, ComputeDescription*, Guid*, nint*, int>)&CreateComputePipeline
            ||
            device[62]
            != (nint)(delegate* unmanaged[Stdcall]<nint, ComputeDescription*, uint, void*, Guid*, nint*, int>)
            &CreateComputePipelineX)
        {
            throw new InvalidOperationException(
                "ABI compute pipeline creation slots 0x58/0x1F0 are not registered correctly.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateComputePipeline(nint self, ComputeDescription* description, Guid* iid, nint* output)
    {
        return CreateComputePipelineCore(description, 0, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateComputePipelineX(nint self, ComputeDescription* description, uint extensionCount,
        void* extensions, Guid* iid, nint* output)
    {
        if (extensionCount != 0 && extensions == null)
        {
            if (output != null)
            {
                *output = 0;
            }

            return InvalidArgument;
        }

        // As upstream, console shader extensions are not interpreted yet.
        return CreateComputePipelineCore(description, extensionCount, iid, output);
    }

    private static int CreateComputePipelineCore(ComputeDescription* description, uint extensionCount, Guid* iid,
        nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null || description->Code == null || description->Size == 0
            || description->Size > MaximumComputeShaderBytes)
        {
            return InvalidArgument;
        }

        if (iid == null || (*iid != PipelineId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId
                            && *iid != PageableId))
        {
            return NoInterface;
        }

        var root = description->Root;
        if (root != 0 && !IsRootSignature(root))
        {
            return InvalidArgument;
        }

        if (output == null)
        {
            return 1; // Validation only; no GPU compilation performed.
        }

        PipelineObject* pipeline = null;
        ShaderContainer? shader = null;
        string? hash = null;
        var shaderRetained = false;
        var rootRetained = false;
        var published = false;
        try
        {
            var bytes = new ReadOnlySpan<byte>(description->Code, (int)description->Size);
            Span<byte> digest = stackalloc byte[32];
            _ = SHA256.HashData(bytes, digest);
            hash = Convert.ToHexString(digest);
            pipeline = (PipelineObject*)NativeMemory.AllocZeroed((nuint)sizeof(PipelineObject));
            pipeline->Header.Table = _pipelineTable;
            pipeline->Header.References = 1;
            lock (PipelineGate)
            {
                if (!ComputeShaders.TryGetValue(hash, out shader))
                {
                    if (_retainedComputeBytes + bytes.Length > MaximumRetainedComputeBytes)
                    {
                        return OutOfMemory;
                    }

                    shader = new ShaderContainer(bytes.ToArray());
                    ComputeShaders.Add(hash, shader);
                    _retainedComputeBytes += shader.Bytes.Length;
                }
                else if (!bytes.SequenceEqual(shader.Bytes))
                {
                    return InvalidArgument;
                }

                shader.References++;
                shaderRetained = true;
                if (root != 0)
                {
                    _ = Retain(root);
                    rootRetained = true;
                }

                ComputePipelines.Add((nint)pipeline, new ComputePipelineState(root, hash, shader));
                LiveObjects[(nint)pipeline] = _pipelineTable;
                published = true;
            }

            *output = (nint)pipeline;
            var created = Interlocked.Increment(ref _computeCreated);
            if (created <= 8 || (created & (created - 1)) == 0)
            {
                Console.WriteLine(
                    $"[graphics] Compute pipeline #{created}: bytes={description->Size}, SHA256={hash[..16]}, extensions={extensionCount} (CPU container only; no host pipeline).");
            }

            return 0;
        }
        catch (OutOfMemoryException) when (!published)
        {
            return OutOfMemory;
        }
        finally
        {
            if (!published)
            {
                lock (PipelineGate)
                {
                    if (pipeline != null)
                    {
                        _ = ComputePipelines.Remove((nint)pipeline);
                        _ = LiveObjects.TryRemove((nint)pipeline, out _);
                    }

                    if (shaderRetained && shader != null && hash != null)
                    {
                        DropShader(hash, shader);
                    }
                }

                if (rootRetained)
                {
                    _ = Drop(root);
                }

                if (pipeline != null)
                {
                    NativeMemory.Free(pipeline);
                }
            }
        }
    }

    // Caller holds PipelineGate.
    private static void DropShader(string hash, ShaderContainer shader)
    {
        shader.References--;
        if (shader.References == 0)
        {
            _ = ComputeShaders.Remove(hash);
            _retainedComputeBytes -= shader.Bytes.Length;
        }
    }

    private static void ReleasePipeline(nint value)
    {
        ComputePipelineState? state;
        GraphicsPipelineMetadata? graphics;
        lock (PipelineGate)
        {
            _ = LiveObjects.TryRemove(value, out _);
            if (ComputePipelines.Remove(value, out state))
            {
                DropShader(state.Hash, state.Shader);
            }

            if (GraphicsPipelines.Remove(value, out graphics))
            {
                foreach (var reference in graphics.Shaders)
                {
                    DropShader(reference.Hash, reference.Container);
                }
            }
        }

        foreach (var key in ResourcePrivateData.Keys)
        {
            if (key.Item1 == value)
            {
                _ = ResourcePrivateData.TryRemove(key, out _);
            }
        }

        NativeMemory.Free((void*)value);
        var root = state?.Root ?? graphics?.Root ?? 0;
        if (root != 0)
        {
            _ = Drop(root);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ComputeDescription
    {
        public nint Root;
        public byte* Code;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PipelineObject
    {
        public Header Header;
        public fixed uint Packet[8];
    }

    private sealed class ShaderContainer(byte[] bytes)
    {
        public readonly byte[] Bytes = bytes;
        public int Inspected;
        public int References;
    }

    private sealed record ComputePipelineState(nint Root, string Hash, ShaderContainer Shader);
}
