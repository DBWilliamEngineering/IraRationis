// SPDX-License-Identifier: GPL-3.0-or-later
// Console pipeline ABI adapted from Zettrox/ztrx victory-gpu/src/pipeline.rs.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Shaders.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly Dictionary<nint, GraphicsPipelineMetadata> GraphicsPipelines = new();
    private static long _graphicsCreated;

    private static void RegisterGraphicsPipelineMethods(nint* device)
    {
        if (sizeof(ShaderBytecode) != 16 || sizeof(GraphicsDescription) != 88
                                         || sizeof(PipelineStreamDescription) != 16)
        {
            throw new InvalidOperationException("Unexpected graphics pipeline ABI layout.");
        }

        device[10] =
            (nint)(delegate* unmanaged[Stdcall]<nint, GraphicsDescription*, Guid*, nint*, int>)&CreateGraphicsPipeline;

        device[86] =
            (nint)(delegate* unmanaged[Stdcall]<nint, PipelineStreamDescription*, Guid*, nint*, int>)
            &CreatePipelineStateStream;
    }

    private static int StreamRecordSize(uint kind)
    {
        return kind switch
        {
            0 or 17 => 16,
            >= 1 and <= 6 or 12 or 19 or 23 or 24 => 24,
            7 or 15 => 40,
            8 => 336,
            10 => 48,
            11 => 56,
            21 => 64,
            22 => 32,
            9 or 13 or 14 or 16 or 18 or 20 => 8,
            _ => 0
        };
    }

    private static bool IsShaderRecord(uint kind)
    {
        return kind is >= 1 and <= 6 or 23 or 24;
    }

    private static bool IsPointerFreeState(uint kind)
    {
        return kind is 8 or 9 or 10 or 11 or 13 or 14 or 15 or 16 or 17 or 18 or 20 or 21;
    }

    // Only semantically empty pointer-bearing state may be omitted. Cached
    // guest PSO data is a performance hint, not executable host state.
    private static bool IsOmittablePipelineState(uint kind, ReadOnlySpan<byte> bytes)
    {
        return kind switch
        {
            7 => bytes.Length == 40 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) == 0 &&
                 BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]) == 0 &&
                 BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]) == 0,
            12 => bytes.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) == 0,
            19 => bytes.Length == 24,
            22 => bytes.Length == 32 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) == 0 &&
                  BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) == 0,
            _ => false
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateGraphicsPipeline(nint self, GraphicsDescription* description, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null)
        {
            return InvalidArgument;
        }

        Span<ShaderBytecode> stages = [description->Vertex, description->Pixel, description->Domain, description->Hull, description->Geometry
        ];

        ReadOnlySpan<uint> kinds = [1, 2, 3, 4, 5];

        return StoreGraphicsPipeline(description->Root, stages, kinds, [], false, false, iid, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreatePipelineStateStream(nint self, PipelineStreamDescription* description, Guid* iid,
        nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null || description->Stream == null || description->Size == 0
            || description->Size > 1024 * 1024)
        {
            return InvalidArgument;
        }

        Span<ShaderBytecode> stages = stackalloc ShaderBytecode[8];

        Span<uint> kinds = stackalloc uint[8];

        var count = 0;
        var root = (nint)0;
        var complete = true;

        string? incompleteReason = null;

        VertexInputElement[]? inputLayout = null;

        var seen = 0U;

        try
        {
            var records = new List<PipelineStateRecord>();
            var length = (int)description->Size;
            var offset = 0;

            while (offset < length)
            {
                if (length - offset < 8)
                {
                    return InvalidArgument;
                }

                var address = description->Stream + offset;
                var kind = Unsafe.ReadUnaligned<uint>(address);
                var size = StreamRecordSize(kind);

                if (size == 0)
                {
                    // Upstream stops at unknown subobjects; never guess their lengths.
                    Console.Error.WriteLine(
                        $"[graphics] Pipeline stream: unknown subobject {kind} at {offset}; remainder not captured.");

                    complete = false;

                    incompleteReason ??= $"unknown subobject {kind} at byte {offset}";

                    break;
                }

                if (size > length - offset)
                {
                    return InvalidArgument;
                }

                var bit = 1U << (int)kind;

                if ((seen & bit) != 0)
                {
                    complete = false;

                    incompleteReason ??= $"duplicate subobject {kind} at byte {offset}";
                }

                seen |= bit;

                if (kind == 0)
                {
                    root = Unsafe.ReadUnaligned<nint>(address + 8);
                }
                else if (IsShaderRecord(kind))
                {
                    if (count == stages.Length || kinds[..count].Contains(kind))
                    {
                        return InvalidArgument;
                    }

                    stages[count] = Unsafe.ReadUnaligned<ShaderBytecode>(address + 8);

                    kinds[count++] = kind;
                }
                else if (IsPointerFreeState(kind))
                {
                    records.Add(new PipelineStateRecord(kind, new ReadOnlySpan<byte>(address, size).ToArray()));
                }
                else if (kind == 12)
                {
                    // Deep-copy the descriptor array and names while the creation arguments are valid.
                    // Never retain the guest's pointer-bearing record itself.
                    if (!TryReadVertexInputLayout(Unsafe.ReadUnaligned<ulong>(address + 8),
                            Unsafe.ReadUnaligned<uint>(address + 16), out inputLayout))
                    {
                        complete = false;

                        incompleteReason ??= $"invalid input layout at byte {offset}";
                    }
                }
                else if (!IsOmittablePipelineState(kind, new ReadOnlySpan<byte>(address, size)))
                {
                    complete = false;

                    incompleteReason ??= $"untranslated pointer-bearing subobject {kind} at byte {offset}";
                }

                offset += size;
            }

            return StoreGraphicsPipeline(root, stages[..count], kinds[..count], records, true, complete, iid, output,
                incompleteReason, inputLayout);
        }
        catch (OutOfMemoryException)
        {
            return OutOfMemory;
        }
    }

    private static int StoreGraphicsPipeline(nint root, ReadOnlySpan<ShaderBytecode> stages, ReadOnlySpan<uint> kinds,
        List<PipelineStateRecord> records, bool stream, bool complete, Guid* iid, nint* output,
        string? incompleteReason = null, VertexInputElement[]? inputLayout = null)
    {
        if (iid == null || (*iid != PipelineId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId
                            && *iid != PageableId))
        {
            return NoInterface;
        }

        if (root != 0 && !IsRootSignature(root))
        {
            return InvalidArgument;
        }

        foreach (var stage in stages)
        {
            if (stage.Size > MaximumComputeShaderBytes || (stage.Size != 0 && stage.Code == null))
            {
                return InvalidArgument;
            }
        }

        if (output == null)
        {
            return 1;
        }

        PipelineObject* pipeline = null;

        List<PipelineShader>? retained = null;

        var rootRetained = false;
        var published = false;

        try
        {
            retained = [with(stages.Length)];

            pipeline = (PipelineObject*)NativeMemory.AllocZeroed((nuint)sizeof(PipelineObject));

            pipeline->Header.Table = _pipelineTable;

            pipeline->Header.References = 1;

            Span<byte> digest = stackalloc byte[32];

            lock (PipelineGate)
            {
                for (var index = 0; index < stages.Length; index++)
                {
                    var stage = stages[index];

                    if (stage.Size == 0)
                    {
                        continue;
                    }

                    var bytes = new ReadOnlySpan<byte>(stage.Code, (int)stage.Size);

                    _ = SHA256.HashData(bytes, digest);

                    var hash = Convert.ToHexString(digest);

                    if (!ComputeShaders.TryGetValue(hash, out var shader))
                    {
                        if (_retainedComputeBytes + bytes.Length > MaximumRetainedComputeBytes)
                        {
                            return OutOfMemory;
                        }

                        shader = new ComputeShaderContainer([.. bytes]);

                        var reference = new PipelineShader(kinds[index], hash, shader);

                        ComputeShaders.Add(hash, shader);

                        _retainedComputeBytes += shader.Bytes.Length;

                        shader.References++;

                        retained.Add(reference);
                    }
                    else
                    {
                        if (!bytes.SequenceEqual(shader.Bytes))
                        {
                            return InvalidArgument;
                        }

                        var reference = new PipelineShader(kinds[index], hash, shader);

                        shader.References++;

                        retained.Add(reference);
                    }
                }

                if (root != 0)
                {
                    _ = Retain(root);

                    rootRetained = true;
                }

                GraphicsPipelines.Add((nint)pipeline,
                    new GraphicsPipelineMetadata(root, retained, records, stream, complete, incompleteReason,
                        inputLayout));

                LiveObjects[(nint)pipeline] = _pipelineTable;

                published = true;
            }

            *output = (nint)pipeline;

            var created = Interlocked.Increment(ref _graphicsCreated);

            if (created <= 8 || (created & (created - 1)) == 0)
            {
                Console.WriteLine(
                    $"[graphics] Graphics pipeline #{created}: stages={retained.Count}, stream={stream}, stateComplete={complete} (CPU metadata only; no host pipeline).");
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
                        _ = GraphicsPipelines.Remove((nint)pipeline);

                        _ = LiveObjects.TryRemove((nint)pipeline, out _);
                    }

                    if (retained != null)
                    {
                        foreach (var reference in retained)
                        {
                            DropShader(reference.Hash, reference.Container);
                        }
                    }
                }

                if (rootRetained)
                {
                    _ = Drop(root);
                }

                NativeMemory.Free(pipeline);
            }
        }
    }
}
