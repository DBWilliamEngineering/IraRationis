// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using IraRationis.Shaders;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int R23ArrayBytes = 256 * 64;
    private const int R14ConstantBytes = 48;
    private const int R14VertexOffset = 256;
    private const int R14UploadCapacity = 1024;
    private static int _r21RectangleInputs;
    private static int _r22IndexedInputs;
    private static int _r23IndexedInputs;
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<nint, R14Pipeline>>
        R14Pipelines = new();

    private static bool TryReadR14InputLayout(ulong address, uint count, out R14InputElement[]? elements)
    {
        elements = null;
        if (count == 0)
        {
            elements = [];

            return true;
        }

        if (count > 32 || address == 0 || 32UL * count > ulong.MaxValue - address || sizeof(R14NativeInput) != 32)
        {
            return false;
        }

        var result = new R14InputElement[(int)count];
        Span<byte> record = stackalloc byte[32];
        Span<byte> name = stackalloc byte[64];
        for (var i = 0; i < result.Length; i++)
        {
            if (!TryReadR12Memory(address + (ulong)i * 32, record))
            {
                return false;
            }

            var pointer = BinaryPrimitives.ReadUInt64LittleEndian(record);
            var length = 0;
            if (pointer == 0 || pointer > ulong.MaxValue - (ulong)name.Length)
            {
                return false;
            }

            for (; length < name.Length; length++)
            {
                if (!TryReadR12Memory(pointer + (ulong)length, name.Slice(length, 1)))
                {
                    return false;
                }

                if (name[length] == 0)
                {
                    break;
                }

                if (name[length] is < 32 or > 126)
                {
                    return false;
                }
            }

            if (length == 0 || length == name.Length)
            {
                return false;
            }

            result[i] = new R14InputElement(Encoding.ASCII.GetString(name[..length]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[28..]));
        }

        elements = result;

        return true;
    }

    private static uint R14ElementBytes(R14InputElement element)
    {
        return (element.Name.ToUpperInvariant(), element.Format) switch
        {
            ("POSITION", 6) => 12,
            ("POSITION", 2) => 16,
            ("COLOR", 2) => 16,
            ("COLOR", 10 or 11) => 8,
            ("COLOR", 24 or 28 or 87) => 4,
            ("TEXCOORD", 16) => 8,
            ("TEXCOORD", 34 or 35) => 4,
            _ => 0
        };
    }

    private static bool TryNormalizeR14Layout(R14InputElement[]? input, uint stride, out R14InputElement[] elements)
    {
        elements = [];
        if (input is not { Length: 3 } || stride is 0 or > 256)
        {
            return false;
        }

        var result = new R14InputElement[input.Length];
        var seen = 0U;
        var next = 0U;
        for (var i = 0; i < input.Length; i++)
        {
            var element = input[i];
            var semantic = element.Name.ToUpperInvariant() switch
            {
                "POSITION" => 1U,
                "COLOR" => 2U,
                "TEXCOORD" => 4U,
                _ => 0U
            };
            var width = R14ElementBytes(element);
            var offset = element.Offset == uint.MaxValue ? next : element.Offset;
            if (semantic == 0 || (seen & semantic) != 0 || element.Index != 0 || element.Slot != 0 ||
                element.Class != 0 || element.Step != 0 || width == 0 || offset > stride || width > stride - offset)
            {
                return false;
            }

            seen |= semantic;
            next = offset + width;
            result[i] = element with { Offset = offset };
        }

        elements = result;

        return seen == 7;
    }

    private static bool TryNormalizeR22Layout(R14InputElement[]? input, uint stride,
        out R14InputElement[] elements)
    {
        elements = [];
        if (stride != 16 || input is not { Length: 3 })
        {
            return false;
        }

        R14InputElement[] expected =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("COLOR", 1, 28, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 8, 0, 0)
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var element = input[i] with { Name = input[i].Name.ToUpperInvariant() };
            if (element != expected[i])
            {
                return false;
            }
        }

        elements = expected;

        return true;
    }

    private static bool RefuseR14(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-DRAW-R14: REFUSED: {reason}.");

        return false;
    }

    private static R14Pipeline? GetR14Pipeline(RecordedCommandState state, uint stride, bool indexedColor)
    {
        GraphicsPipelineMetadata? metadata;
        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out metadata);
        }

        R14InputElement[] elements;
        if (metadata == null || metadata.Root != state.Graphics.Signature || !IsRootSignature(metadata.Root) ||
            !metadata.IsStream || !metadata.CompleteStream || metadata.Shaders.Count != 2 ||
            !(indexedColor
                ? TryNormalizeR22Layout(metadata.InputLayout, stride, out elements)
                : TryNormalizeR14Layout(metadata.InputLayout, stride, out elements)))
        {
            _ = RefuseR14("pipeline incomplete or unsupported input elements (see draw report)");

            return null;
        }

        var cache = R14Pipelines.GetOrCreateValue(state);
        if (cache.TryGetValue(state.Pipeline, out var cached))
        {
            return cached;
        }

        var vertex = metadata.Shaders.SingleOrDefault(static stage => stage.Stage == 1);
        var pixel = metadata.Shaders.SingleOrDefault(static stage => stage.Stage == 2);
        var depth = metadata.State.SingleOrDefault(static record => record.Kind == 21);
        if (vertex == null || pixel == null || depth == null || depth.Bytes.Length != 64 ||
            BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(56)) != 0)
        {
            _ = RefuseR14("shader stages or depth bounds unsupported");

            return null;
        }

        if (R21RectanglePipelines.Acquire(metadata, state) is { } shared)
        {
            cache.Add(state.Pipeline, shared);

            return shared;
        }

        void* root = null;
        nint pipeline = 0;
        try
        {
            var report = Console.Error;
            var rootBytes = RootSignatureBlob(metadata.Root).ToArray();
            var pair = indexedColor
                ? Rdna2RectangleCompiler.TranslateIndexedColor(vertex.Container.Bytes, pixel.Container.Bytes, rootBytes)
                : Rdna2RectangleCompiler.Translate(vertex.Container.Bytes, pixel.Container.Bytes, rootBytes);
            report.WriteLine(indexedColor
                ? pair.IndexedArray
                    ? "[graphics] IRX-GFX-SHADER-R23: recognized indexed array color transform; up to 256 parameter blocks."
                    : "[graphics] IRX-GFX-SHADER-R22: recognized indexed triangle color transform; 64-byte vertex CBV."
                : "[graphics] IRX-GFX-SHADER-R14: recognized rectangle IA fetch / perspective COLOR / CBV modulation; b27, space1.");
            var vs = CompileR10Shader(pair.VertexSource, "vs_5_1", report);
            var gs = indexedColor ? null : CompileR10Shader(pair.GeometrySource, "gs_5_1", report);
            var ps = CompileR10Shader(pair.PixelSource, "ps_5_1", report);
            // The descriptor table is lowered to a root CBV. It is populated only after the preceding guest work.
            var contract = new VertexFetchShaderPair(string.Empty, string.Empty, pair.Register, pair.Space, default,
                default);
            var blob = SerializeR10HostRoot(contract, report, true);
            if (vs == null || (!indexedColor && gs == null) || ps == null || blob == null)
            {
                _ = RefuseR14("host shader compilation or root serialization failed");

                return null;
            }

            var iid = RootSignatureId;
            fixed (byte* bytes = blob)
            {
                var hr = (int)_host->CreateRootSignature(0, bytes, (nuint)blob.Length, &iid, &root);
                if (hr < 0 || root == null)
                {
                    _ = RefuseR14($"host root creation HRESULT=0x{hr:X8}");

                    return null;
                }
            }

            var native = stackalloc R14NativeInput[3];
            fixed (byte* position = "POSITION\0"u8)
            fixed (byte* color = "COLOR\0"u8)
            fixed (byte* uv = "TEXCOORD\0"u8)
            {
                for (var i = 0; i < elements.Length; i++)
                {
                    var element = elements[i];
                    native[i] = new R14NativeInput
                    {
                        Name = element.Name.ToUpperInvariant() switch
                        {
                            "POSITION" => (nint)position,
                            "COLOR" => (nint)color,
                            _ => (nint)uv
                        },
                        Index = element.Index,
                        Format = element.Format,
                        Slot = element.Slot,
                        Offset = element.Offset,
                        Class = element.Class,
                        Step = element.Step
                    };
                    report.WriteLine(
                        $"[graphics] R14 input {element.Name}: format={element.Format}, offset={element.Offset}.");
                }

                pipeline = CreateR10PipelineCandidate(root, vs, ps, metadata, report, gs, native,
                    (uint)elements.Length);
            }

            if (pipeline == 0)
            {
                _ = RefuseR14("host rectangle PSO creation failed");

                return null;
            }

            var result = new R14Pipeline((nint)root, pipeline, elements, pair.IndexedArray);
            state.HostViews.Add((nint)root);
            root = null;
            state.HostViews.Add(pipeline);
            pipeline = 0;
            cache.Add(state.Pipeline, result);
            R21RectanglePipelines.Publish(metadata, result);

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                              NotSupportedException or OverflowException or DllNotFoundException or
                                              EntryPointNotFoundException)
        {
            _ = RefuseR14(exception.Message);

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

    private static bool TryRecordR14GuestDraw(nint self, uint vertices, uint instances, uint first, uint firstInstance,
        bool indexed = false, int baseVertex = 0)
    {
        using var r25Timing = BeginR25Timing(R25Stage.RecordRectangle);
        if (!ExecuteGuestVertexFetch)
        {
            return RefuseR14("guest execution explicitly disabled");
        }

        var state = StateFor(self);
        var indexedColor = indexed && state.Topology == 4;
        if (_host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 || instances != 1 ||
            (!indexed && (vertices != 3 || first != 0)) || firstInstance != 0
            || (!indexedColor && state.Topology != R14RectangleTopology) ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } ||
            state.Targets.Length != 1 || state.Depth is not { } depthHandle || state.Vertices.Count != 1 ||
            !state.Vertices.TryGetValue(0, out var vertex) || state.Graphics.Tables.Count != 1 ||
            !state.Graphics.Tables.TryGetValue(0, out var table) || state.Graphics.Views.Count != 0 ||
            (indexedColor
                ? state.Graphics.Constants.Count != 1 || !state.Graphics.Constants.ContainsKey(1)
                : state.Graphics.Constants.Count != 0))
        {
            return RefuseR14("unsupported rectangle draw or binding shape");
        }

        var stride = vertex.FormatOrStride;
        R20IndexedUploadPlan? plan = null;
        if (indexed)
        {
            if (vertices == 0 || vertices % 3 != 0 || state.Index is not { } index ||
                !TryPlanR20IndexedUpload(vertex, index, vertices, first, baseVertex, out plan))
            {
                return RefuseR14("invalid or unsupported indexed input assembly extent");
            }
        }

        var capacity = plan?.Capacity ?? R14UploadCapacity;
        if (stride is 0 or > 256 || vertex.Size < stride * 3U || vertex.Address == 0 || table == 0 ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2 ||
            state.HostViews.Count > R13MaximumHostViews - 5 || (ulong)capacity > UploadBudget ||
            state.UploadBytes > UploadBudget - (ulong)capacity)
        {
            return RefuseR14("vertex extent, descriptor or command-list budget invalid");
        }

        var translated = GetR14Pipeline(state, stride, indexedColor);
        if (translated == null)
        {
            return false;
        }

        var constantOffset = 0;
        if (translated.IndexedArray)
        {
            if (plan == null || !indexedColor)
            {
                return RefuseR14("indexed array pipeline requires indexed triangle input");
            }

            constantOffset = checked((capacity + 255) & ~255);
            capacity = checked(constantOffset + R23ArrayBytes);
            if (capacity > R20MaximumIndexedUpload || (ulong)capacity > UploadBudget ||
                state.UploadBytes > UploadBudget - (ulong)capacity)
            {
                return RefuseR14("indexed array upload exceeds budget");
            }
        }

        if (!TryResolveClear(self, state.Targets[0], 20, out var color, out var rtv, out var colorFormat) ||
            colorFormat != 28
            || !TryResolveClear(self, depthHandle, 21, out var depth, out var dsv, out var depthFormat) ||
            depthFormat != 20 || color == depth)
        {
            return RefuseR14("unsupported target views");
        }

        nint upload = 0;
        try
        {
            var hr = CreateHostResource(HostBufferDescription((ulong)capacity), 2, GenericRead, &upload);
            if (hr < 0 || upload == 0)
            {
                return RefuseR14($"host upload allocation HRESULT=0x{hr:X8}");
            }

            var gpu = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();
            if (gpu == 0 || (gpu & 255UL) != 0)
            {
                return RefuseR14("host CBV alignment invalid");
            }

            var deferred = new R14Upload(table, vertex.Address, stride, upload)
            {
                Target = R16FindOwnedResource(state, color),
                Indexed = plan,
                IndexedColor = indexedColor,
                IndexedArray = translated.IndexedArray,
                ConstantOffset = constantOffset,
                Capacity = capacity,
                ConstantBytes = translated.IndexedArray ? R23ArrayBytes : indexedColor ? 64 : R14ConstantBytes
            };
            state.HostViews.Add(upload);
            upload = 0;
            state.UploadBytes += (ulong)capacity;
            SealMemoryBoundary(self, state, null, null, rectangle: new R14DrawBoundary(deferred, false));
            var list = Host(self);
            var commands = (ID3D12GraphicsCommandList*)list;
            var vb = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = gpu + R14VertexOffset,
                SizeInBytes = plan == null ? stride * 3U : (uint)plan.VertexBytes,
                StrideInBytes = stride
            };
            var colorView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = rtv };
            var depthView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = dsv };
            TransitionHostResource(list, color, 0, 4);
            TransitionHostResource(list, depth, 0, 0x10);
            commands->SetPipelineState((ID3D12PipelineState*)translated.Pipeline);
            commands->SetGraphicsRootSignature((ID3D12RootSignature*)translated.Root);
            commands->SetGraphicsRootConstantBufferView(0, gpu + (ulong)constantOffset);
            commands->IASetVertexBuffers(0, 1, &vb);
            commands->OMSetRenderTargets(1, &colorView, false, &depthView);
            if (plan != null)
            {
                var ib = new D3D12_INDEX_BUFFER_VIEW
                {
                    BufferLocation = gpu + (ulong)plan.IndexOffset,
                    SizeInBytes = (uint)plan.IndexBytes,
                    Format = (DXGI_FORMAT)plan.IndexFormat
                };
                commands->IASetIndexBuffer(&ib);
                TraceHostCommand(self,
                    indexedColor ? "R22 triangle DrawIndexedInstanced" : "R20 rectangle DrawIndexedInstanced");
                // The staged index view starts at the requested first index.
                commands->DrawIndexedInstanced(vertices, instances, 0, baseVertex, firstInstance);
            }
            else
            {
                TraceHostCommand(self, "R14 rectangle DrawInstanced");
                commands->DrawInstanced(vertices, instances, first, firstInstance);
            }

            commands->OMSetRenderTargets(0, null, false, null);
            TransitionHostResource(list, depth, 0x10, 0);
            TransitionHostResource(list, color, 4, 0);
            SealMemoryBoundary(self, state, null, null, rectangle: new R14DrawBoundary(deferred, true));
            TraceR24(indexedColor
                ? $"[graphics] IRX-GFX-INDEX-{(translated.IndexedArray ? "R23" : "R22")}: RECORDED indexed triangle; count={vertices}, first={first}, baseVertex={baseVertex}, awaiting submission."
                : plan == null
                    ? "[graphics] IRX-GFX-DRAW-R14: RECORDED guest rectangle; awaiting submission."
                    : $"[graphics] IRX-GFX-INDEX-R20: RECORDED indexed rectangle; count={vertices}, first={first}, baseVertex={baseVertex}, awaiting submission.");

            return true;
        }
        finally
        {
            if (upload != 0)
            {
                _ = HostRelease(upload);
            }
        }
    }

    private static void ExecuteR14DrawBoundary(nint queue, R14DrawBoundary boundary)
    {
        using var r25Timing = BeginR25Timing(R25Stage.ExecuteRectangle);
        var upload = boundary.Upload;
        if (boundary.Complete)
        {
            if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
            {
                Environment.FailFast("[graphics] R14 completion before source preparation.");
            }

            MarkR16GpuWrite(upload.Target, queue);
            Interlocked.Increment(ref _r24Draws);
            if (!upload.IndexedColor)
            {
                TraceR24("[graphics] IRX-GFX-DRAW-R14: GPU COMPLETED guest rectangle (presentation unverified).");
            }

            TraceR171Color(queue, upload.Target, upload.IndexedArray ? "R23-indexed-output" :
                upload.IndexedColor ? "R22-indexed-output" :
                upload.Indexed == null ? "R14-output" : "R20-indexed-output");
            if (upload.Indexed != null)
            {
                TraceR24(upload.IndexedColor
                    ? $"[graphics] IRX-GFX-INDEX-{(upload.IndexedArray ? "R23" : "R22")}: GPU COMPLETED indexed triangle (content unverified)."
                    : "[graphics] IRX-GFX-INDEX-R20: GPU COMPLETED indexed rectangle (content unverified).");
            }

            return;
        }

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0)
        {
            Environment.FailFast("[graphics] R14 experimental draw cannot be resubmitted without reset.");
        }

        Span<byte> token = stackalloc byte[16];
        Span<byte> recheck = stackalloc byte[16];
        var plan = upload.Indexed;
        var capacity = upload.Capacity;
        var rented = plan != null && capacity <= 4 * 1024 * 1024
            ? ArrayPool<byte>.Shared.Rent(capacity)
            : null;
        try
        {
            var snapshot = plan == null ? stackalloc byte[R14UploadCapacity] :
                rented == null ? new byte[capacity] : rented.AsSpan(0, capacity);
            snapshot.Clear();
            var vertexBytes = plan?.VertexBytes ?? checked((int)(upload.Stride * 3U));
            token.Clear();
            if (!TryReadR12Memory(upload.Table, token) || BinaryPrimitives.ReadUInt32LittleEndian(token) != ConstantTag)
            {
                Environment.FailFast($"[graphics] R14/R22/R23 submission-time descriptor is not an IRXC CBV token; " +
                                     $"tag=0x{BinaryPrimitives.ReadUInt32LittleEndian(token):X8}.");
            }

            var constants = BinaryPrimitives.ReadUInt64LittleEndian(token[8..]);
            var vertices = snapshot.Slice(R14VertexOffset, vertexBytes);
            if (!TryReadR12Memory(upload.Vertex, vertices) ||
                (plan != null
                 && (!TryReadR12Memory(plan.IndexAddress, snapshot.Slice(plan.IndexOffset, plan.IndexBytes)) ||
                     !ValidateR20Indices(plan, snapshot.Slice(plan.IndexOffset, plan.IndexBytes)))))
            {
                Environment.FailFast(
                    "[graphics] R14/R20 submission-time vertex/index source unreadable or index out of range.");
            }

            var requiredBytes = upload.ConstantBytes;
            if (upload.IndexedArray)
            {
                if (plan == null || !TryR23ConstantExtent(plan, vertices,
                        snapshot.Slice(plan.IndexOffset, plan.IndexBytes),
                        out requiredBytes))
                {
                    Environment.FailFast("[graphics] R23 cannot determine indexed constant extent.");
                }
            }

            var declaredBytes = BinaryPrimitives.ReadUInt32LittleEndian(token[4..]);
            if (constants == 0 || declaredBytes < (uint)requiredBytes ||
                constants > ulong.MaxValue - (ulong)requiredBytes ||
                !TryReadR12Memory(constants, snapshot.Slice(upload.ConstantOffset, requiredBytes)) ||
                !TryReadR12Memory(upload.Table, recheck) || !token.SequenceEqual(recheck))
            {
                Environment.FailFast($"[graphics] R14/R22/R23 constant source unreadable or changed; " +
                                     $"declared={declaredBytes}, required={requiredBytes}.");
            }

            if (EnableR171ColorFlow && plan == null)
            {
                var observation = Interlocked.Increment(ref _r21RectangleInputs);
                if (observation <= 4 || (observation <= 64 && (observation & (observation - 1)) == 0))
                {
                    var m = snapshot.Slice(32, 16);
                    var multiplier = FormattableString.Invariant(
                        $"({BinaryPrimitives.ReadSingleLittleEndian(m):G9}, {BinaryPrimitives.ReadSingleLittleEndian(m[4..]):G9}, {BinaryPrimitives.ReadSingleLittleEndian(m[8..]):G9}, {BinaryPrimitives.ReadSingleLittleEndian(m[12..]):G9})");
                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-INPUT-R21: R14 observation={observation}, CBV multiplier={multiplier}, stride={upload.Stride}, vertexBytes={Convert.ToHexString(snapshot.Slice(R14VertexOffset, vertexBytes))}.");
                }
            }

            if (upload.IndexedColor && !upload.IndexedArray && plan != null && EnableR171ColorFlow)
            {
                var observation = Interlocked.Increment(ref _r22IndexedInputs);
                if (observation <= 4 || (observation <= 64 && (observation & (observation - 1)) == 0))
                {
                    var indices = snapshot.Slice(plan.IndexOffset, plan.IndexBytes);
                    var firstIndex = plan.IndexFormat == 57
                        ? BinaryPrimitives.ReadUInt16LittleEndian(indices)
                        : BinaryPrimitives.ReadUInt32LittleEndian(indices);
                    var effective = firstIndex + plan.BaseVertex;
                    var vertexOffset = checked(R14VertexOffset + (int)(effective * plan.VertexStride));
                    var firstVertex = snapshot.Slice(vertexOffset, 16);
                    Console.Error.WriteLine($"[graphics] IRX-GFX-INPUT-R22: observation={observation}, " +
                                            $"firstIndex={firstIndex}, effectiveVertex={effective}, firstVertex={Convert.ToHexString(firstVertex)}, "
                                            +
                                            $"CBV64={Convert.ToHexString(snapshot[..64])}.");
                }
            }

            if (upload.IndexedArray && EnableR171ColorFlow)
            {
                var observation = Interlocked.Increment(ref _r23IndexedInputs);
                if (observation <= 4 || (observation <= 64 && (observation & (observation - 1)) == 0))
                {
                    Console.Error.WriteLine($"[graphics] IRX-GFX-INPUT-R23: observation={observation}, " +
                                            $"CBV declared={declaredBytes}, referencedExtent={requiredBytes}, blocks={requiredBytes / 64}.");
                }
            }

            var noRead = default(HostRange);
            void* mapped = null;
            var hr = ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload.Host, 8))
                (upload.Host, 0, &noRead, &mapped);
            if (hr < 0 || mapped == null)
            {
                Environment.FailFast($"[graphics] R14 host upload Map HRESULT=0x{hr:X8}.");
            }

            var written = default(HostRange);
            try
            {
                snapshot.CopyTo(new Span<byte>(mapped, capacity));
                written.End = (nuint)capacity;
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload.Host, 9))
                    (upload.Host, 0, &written);
            }

            TraceR24(
                $"[graphics] IRX-GFX-DRAW-R14: UPLOADED {vertexBytes} vertex bytes and {requiredBytes} CBV bytes.");
            if (plan != null)
            {
                var stage = upload.IndexedArray ? "R23" : upload.IndexedColor ? "R22" : "R20";
                TraceR24(
                    $"[graphics] IRX-GFX-INDEX-{stage}: UPLOADED {plan.IndexBytes} index bytes; all referenced vertices within view.");
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented, true);
            }
        }
    }

    private static bool TryR23ConstantExtent(R20IndexedUploadPlan plan, ReadOnlySpan<byte> vertices,
        ReadOnlySpan<byte> indices, out int bytes)
    {
        bytes = 0;
        if (plan.VertexStride != 16 || vertices.Length != plan.VertexBytes || indices.IsEmpty ||
            !ValidateR20Indices(plan, indices))
        {
            return false;
        }

        var size = plan.IndexFormat == 57 ? 2 : 4;
        for (var offset = 0; offset < indices.Length; offset += size)
        {
            var raw = size == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(indices[offset..])
                : BinaryPrimitives.ReadUInt32LittleEndian(indices[offset..]);
            var effective = raw + plan.BaseVertex;
            var blue = vertices[checked((int)(effective * 16 + 6))];
            bytes = Math.Max(bytes, (blue + 1) * 64);
        }

        return bytes is > 0 and <= R23ArrayBytes;
    }

    private static void VerifyR23ConstantExtent()
    {
        var plan = new R20IndexedUploadPlan(0x1000, 57, 48, 16, 304, 6, 310, -1);
        Span<byte> vertices = stackalloc byte[48];
        vertices.Clear();
        vertices[6] = 5;
        vertices[22] = 255;
        vertices[38] = 8;
        if (!TryR23ConstantExtent(plan, vertices, [1, 0, 3, 0, 1, 0], out var small) || small != 576 ||
            !TryR23ConstantExtent(plan, vertices, [2, 0, 3, 0, 1, 0], out var large) || large != R23ArrayBytes ||
            TryR23ConstantExtent(plan, vertices, [0, 0, 1, 0, 2, 0], out _))
        {
            throw new InvalidOperationException("R23 indexed constant extent regression failed.");
        }

        Console.WriteLine("[graphics-probe] R23 indexed color constant extent passed (CPU only).");
    }

    private static void VerifyR14InputLayout()
    {
        VerifyR20IndexedUpload();
        VerifyR23ConstantExtent();
        R14InputElement[] sample =
        [
            new("POSITION", 0, 6, 0, 0, 0, 0),
            new("COLOR", 0, 28, 0, uint.MaxValue, 0, 0),
            new("TEXCOORD", 0, 16, 0, uint.MaxValue, 0, 0)
        ];
        if (sizeof(R14NativeInput) != 32 || !TryNormalizeR14Layout(sample, 24, out var resolved) ||
            resolved[1].Offset != 12 || resolved[2].Offset != 16 || TryNormalizeR14Layout(sample, 23, out _) ||
            HostRasterTopology(17) != 4 || HostRasterTopology(4) != 4)
        {
            throw new InvalidOperationException("R14 input-layout or topology regression failed.");
        }

        R14InputElement[] indexedSample =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("COLOR", 1, 28, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 8, 0, 0)
        ];
        if (!TryNormalizeR22Layout(indexedSample, 16, out var indexedElements) ||
            indexedElements[1].Index != 1 || TryNormalizeR22Layout(indexedSample, 15, out _))
        {
            throw new InvalidOperationException("R22 indexed color input layout regression failed.");
        }

        indexedSample[1] = indexedSample[1] with { Index = 0 };
        if (TryNormalizeR22Layout(indexedSample, 16, out _))
        {
            throw new InvalidOperationException("R22 duplicated COLOR semantic accepted.");
        }

        Console.WriteLine("[graphics-probe] R22 indexed color input layout validation passed (CPU only).");

        if (OperatingSystem.IsWindows())
        {
            Span<byte> semantic = stackalloc byte[9];
            "POSITION\0"u8.CopyTo(semantic);
            fixed (byte* name = semantic)
            {
                var native = new R14NativeInput { Name = (nint)name, Format = 6 };
                if (!TryReadR14InputLayout((nuint)(&native), 1, out var copied) ||
                    copied is not { Length: 1 })
                {
                    throw new InvalidOperationException("R14 input descriptor deep copy failed.");
                }

                native.Format = 28;
                semantic[0] = (byte)'X';
                if (copied[0].Name != "POSITION" || copied[0].Format != 6)
                {
                    throw new InvalidOperationException("R14 input descriptor retained mutable guest state.");
                }
            }
        }

        Console.WriteLine("[graphics-probe] R14 rectangle input layout and topology validation passed (CPU only).");
    }

    private sealed record R14InputElement(
        string Name,
        uint Index,
        uint Format,
        uint Slot,
        uint Offset,
        uint Class,
        uint Step);

    private sealed record R14Pipeline(
        nint Root,
        nint Pipeline,
        R14InputElement[] Elements,
        bool IndexedArray = false);

    private sealed class R14Upload(ulong table, ulong vertex, uint stride, nint host)
    {
        public readonly nint Host = host;
        public readonly uint Stride = stride;
        public readonly ulong Table = table;
        public readonly ulong Vertex = vertex;
        public int Capacity;
        public int ConstantBytes = R14ConstantBytes;
        public int ConstantOffset;
        public R20IndexedUploadPlan? Indexed;
        public bool IndexedArray;
        public bool IndexedColor;
        public int Phase;
        public nint Target;
    }

    private readonly record struct R14DrawBoundary(R14Upload Upload, bool Complete);

    [StructLayout(LayoutKind.Sequential)]
    private struct R14NativeInput
    {
        public nint Name;
        public uint Index;
        public uint Format;
        public uint Slot;
        public uint Offset;
        public uint Class;
        public uint Step;
    }
}
