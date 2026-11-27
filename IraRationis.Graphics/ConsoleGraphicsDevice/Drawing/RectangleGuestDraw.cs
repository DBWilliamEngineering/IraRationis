// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Drawing;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Shaders.Raster;
using IraRationis.Shaders.VertexFetch.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int RectangleConstantArrayBytes = 256 * 64;
    internal const int RectangleConstantBytes = 48;
    private const int RectangleVertexOffset = 256;
    private const int RectangleStackSnapshotLimit = 1024;
    private const int MaximumNonIndexedRectangleUpload = 4 * 1024 * 1024;
    private static int _rectangleInputReports;
    private static int _indexedLayoutReports;
    private static int _indexedConstantReports;
    private static readonly bool EnableNarrowVertexReads =
        Environment.GetEnvironmentVariable("IRX_NARROW_VERTEX_READS") != "0";
    private static int _vertexReads;
    private static readonly bool EnableSparseUpload =
        Environment.GetEnvironmentVariable("IRX_SPARSE_INDEX_UPLOAD") == "1";
    private static int _sparseUploads;
    private static readonly ConditionalWeakTable<RecordedCommandState,
        Dictionary<(nint Pipeline, nint Root, uint Stride, bool IndexedColor, bool DepthBound, uint ColorFormat), RectanglePipeline>>
        RectanglePipelines = new();

    private static bool TryReadVertexInputLayout(ulong address, uint count, out VertexInputElement[]? elements)
    {
        elements = null;

        if (count == 0)
        {
            elements = [];

            return true;
        }

        if (count > 32 || address == 0 || 32UL * count > ulong.MaxValue - address || sizeof(NativeVertexInputElement) != 32)
        {
            return false;
        }

        var result = new VertexInputElement[(int)count];

        Span<byte> record = stackalloc byte[32];
        Span<byte> name = stackalloc byte[64];

        for (var index = 0; index < result.Length; index++)
        {
            if (!TryReadGuestMemory(address + (ulong)index * 32, record))
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
                if (!TryReadGuestMemory(pointer + (ulong)length, name.Slice(length, 1)))
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

            result[index] = new VertexInputElement(Encoding.ASCII.GetString(name[..length]),
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

    private static uint RectangleElementBytes(VertexInputElement element)
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

    private static bool TryNormalizeRectangleLayout(VertexInputElement[]? input, uint stride, out VertexInputElement[] elements)
    {
        elements = [];

        if (input is not { Length: 3 } || stride is 0 or > 256)
        {
            return false;
        }

        var result = new VertexInputElement[input.Length];
        var seen = 0U;
        var next = 0U;

        for (var index = 0; index < input.Length; index++)
        {
            var element = input[index];
            var semantic = element.Name.ToUpperInvariant() switch
            {
                "POSITION" => 1U,
                "COLOR" => 2U,
                "TEXCOORD" => 4U,
                _ => 0U
            };
            var width = RectangleElementBytes(element);
            var offset = element.Offset == uint.MaxValue ? next : element.Offset;

            if (semantic == 0 || (seen & semantic) != 0 || element.Index != 0 || element.Slot != 0 ||
                element.Class != 0 || element.Step != 0 || width == 0 || offset > stride || width > stride - offset)
            {
                return false;
            }

            seen |= semantic;

            next = offset + width;

            result[index] = element with
            {
                Offset = offset
            };
        }

        elements = result;

        return seen == 7;
    }

    private static bool TryNormalizeIndexedRectangleLayout(VertexInputElement[]? input, uint stride,
        out VertexInputElement[] elements)
    {
        elements = [];

        if (stride != 16 || input is not { Length: 3 })
        {
            return false;
        }

        VertexInputElement[] expected =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("COLOR", 1, 28, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 8, 0, 0)
        ];

        for (var index = 0; index < expected.Length; index++)
        {
            var element = input[index] with
            {
                Name = input[index].Name.ToUpperInvariant()
            };

            if (element != expected[index])
            {
                return false;
            }
        }

        elements = expected;

        return true;
    }

    private static bool RejectRectangleDraw(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-DRAW-R14: REFUSED: {reason}.");

        return false;
    }

    private static bool SupportsRectangleTargets(uint colorFormat, bool indexedColor, bool depthBound)
    {
        return colorFormat switch
        {
            HostTextureViewFormats.A8Unorm => indexedColor && !depthBound,
            HostTextureViewFormats.Rgba8Unorm => !indexedColor || depthBound,
            _ => false
        };
    }

    private static bool TryPrepareRectangleHostState(GraphicsPipelineMetadata guest, uint colorFormat,
        bool indexedColor, bool depthBound, out GraphicsPipelineMetadata host)
    {
        host = guest;

        if (!SupportsRectangleTargets(colorFormat, indexedColor, depthBound) ||
            !DepthTargetPolicy.TryGetHostState(guest, depthBound, out host) ||
            (colorFormat == HostTextureViewFormats.A8Unorm && !ReferenceEquals(guest, host)))
        {
            return false;
        }

        var targets = host.State.SingleOrDefault(static record => record.Kind == 15);

        return targets is { Bytes.Length: 40 } &&
               BinaryPrimitives.ReadUInt32LittleEndian(targets.Bytes) == 15 &&
               IsPipelineStateCompatible(targets, rectangle: !indexedColor, allowDepthless: !depthBound, colorFormat: colorFormat);
    }

    private static RectanglePipeline? GetRectanglePipeline(RecordedCommandState state, uint stride, bool indexedColor,
        bool depthBound, uint colorFormat)
    {
        var rootSignature = state.Graphics.Signature;

        if (!IsRootSignature(rootSignature))
        {
            _ = RejectRectangleDraw("missing or invalid bound root signature");

            return null;
        }

        var cache = RectanglePipelines.GetOrCreateValue(state);
        var key = (state.Pipeline, Root: rootSignature, Stride: stride, IndexedColor: indexedColor, DepthBound: depthBound,
            ColorFormat: colorFormat);

        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        GraphicsPipelineMetadata? metadata;

        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out metadata);
        }

        VertexInputElement[] elements;

        if (metadata == null || metadata.Root != rootSignature ||
            !metadata.IsStream || !metadata.CompleteStream || metadata.Shaders.Count != 2 ||
            !(indexedColor
                ? TryNormalizeIndexedRectangleLayout(metadata.InputLayout, stride, out elements)
                : TryNormalizeRectangleLayout(metadata.InputLayout, stride, out elements)))
        {
            _ = RejectRectangleDraw("pipeline incomplete or unsupported input elements (see draw report)");

            return null;
        }

        if (!TryPrepareRectangleHostState(metadata, colorFormat, indexedColor, depthBound, out var hostMetadata))
        {
            _ = RejectRectangleDraw("pipeline output state is unsupported for the bound targets");

            return null;
        }

        var vertex = metadata.Shaders.SingleOrDefault(static stage => stage.Stage == 1);
        var pixel = metadata.Shaders.SingleOrDefault(static stage => stage.Stage == 2);
        if (vertex == null || pixel == null)
        {
            _ = RejectRectangleDraw("shader stages unsupported");

            return null;
        }

        if (SharedRectanglePipelines.Acquire(metadata, state) is { } shared)
        {
            cache.Add(key, shared);

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
            var dualMask = pair.SamplesDualMasks;

            if (dualMask && !IsPositionColorUvLayout(elements))
            {
                _ = RejectRectangleDraw("dual-mask shader requires POSITION float3, COLOR RGBA8 and TEXCOORD float2");

                return null;
            }

            report.WriteLine(dualMask
                ? "[graphics] Recognized rectangle with two sampled alpha masks."
                : indexedColor
                ? pair.IndexedArray
                    ? "[graphics] IRX-GFX-SHADER-R23: recognized indexed array color transform; up to 256 parameter blocks."
                    : "[graphics] IRX-GFX-SHADER-R22: recognized indexed triangle color transform; 64-byte vertex CBV."
                : "[graphics] IRX-GFX-SHADER-R14: recognized rectangle IA fetch / perspective COLOR / CBV modulation; b27, space1.");

            var vertexShader = CompileHostShader(pair.VertexSource, "vs_5_1", report);
            var geometryShader = indexedColor ? null : CompileHostShader(pair.GeometrySource, "gs_5_1", report);
            var pixelShader = CompileHostShader(pair.PixelSource, "ps_5_1", report);
            // The descriptor table is lowered to a root CBV. It is populated only after the preceding guest work.
            var contract = new VertexFetchShaderPair(string.Empty, string.Empty, pair.Register, pair.Space, default,
                default);
            var blob = SerializeHostRoot(contract, report, rectangle: true, dualMask: dualMask);

            if (vertexShader == null || (!indexedColor && geometryShader == null) || pixelShader == null || blob == null)
            {
                _ = RejectRectangleDraw("host shader compilation or root serialization failed");

                return null;
            }

            var iid = RootSignatureId;

            fixed (byte* bytes = blob)
            {
                var resultCode = (int)_host->CreateRootSignature(0, bytes, (nuint)blob.Length, &iid, &root);

                if (resultCode < 0 || root == null)
                {
                    _ = RejectRectangleDraw($"host root creation HRESULT=0x{resultCode:X8}");

                    return null;
                }
            }

            var native = stackalloc NativeVertexInputElement[3];

            fixed (byte* position = "POSITION\0"u8)
            fixed (byte* color = "COLOR\0"u8)
            fixed (byte* textureCoordinateSemantic = "TEXCOORD\0"u8)
            {
                for (var index = 0; index < elements.Length; index++)
                {
                    var element = elements[index];

                    native[index] = new NativeVertexInputElement
                    {
                        Name = element.Name.ToUpperInvariant() switch
                        {
                            "POSITION" => (nint)position,
                            "COLOR" => (nint)color,
                            _ => (nint)textureCoordinateSemantic
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

                pipeline = CreatePipelineCandidate(root, vertexShader, pixelShader, hostMetadata, report, geometryShader,
                    native, (uint)elements.Length, allowDepthless: !depthBound, colorFormat: colorFormat);
            }

            if (pipeline == 0)
            {
                _ = RejectRectangleDraw("host rectangle PSO creation failed");

                return null;
            }

            var result = new RectanglePipeline((nint)root, pipeline, elements, pair.IndexedArray, dualMask);

            state.HostViews.Add((nint)root);

            root = null;

            state.HostViews.Add(pipeline);

            pipeline = 0;

            cache.Add(key, result);

            SharedRectanglePipelines.Publish(metadata, result);

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                              NotSupportedException or OverflowException or DllNotFoundException or
                                              EntryPointNotFoundException)
        {
            _ = RejectRectangleDraw(exception.Message);

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

    private static bool TryGetRectangleVertexRange(RecordedBufferView vertex, uint first, uint count,
        out (ulong Address, int Bytes) range)
    {
        range = default;

        var stride = vertex.FormatOrStride;
        var start = (ulong)first * stride;
        var bytes = (ulong)count * stride;
        var addressLimit = (ulong)nint.MaxValue;

        if (stride is 0 or > 256 || count == 0 || count % 3 != 0 || vertex.Address == 0 ||
            start > vertex.Size || bytes > vertex.Size - start ||
            bytes > MaximumNonIndexedRectangleUpload - RectangleVertexOffset ||
            vertex.Address > addressLimit || start > addressLimit - vertex.Address ||
            bytes > addressLimit - vertex.Address - start)
        {
            return false;
        }

        range = (vertex.Address + start, (int)bytes);

        return true;
    }

    private static bool TryRecordRectangleGuestDraw(nint self, uint vertices, uint instances, uint first, uint firstInstance,
        bool indexed = false, int baseVertex = 0)
    {
        using var stageTiming = BeginTiming(GraphicsStage.RecordRectangle);

        if (!ExecuteGuestVertexFetch)
        {
            return RejectRectangleDraw("guest execution explicitly disabled");
        }

        var state = StateFor(self);
        var indexedColor = indexed && state.Topology == 4;
        var depthHandle = state.Depth;

        if (_host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 || instances != 1 ||
            firstInstance != 0
            || (!indexedColor && state.Topology != RectangleRectangleTopology) ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } ||
            state.Targets.Length != 1 || (indexed && !indexedColor && !depthHandle.HasValue) || state.Vertices.Count != 1 ||
            !state.Vertices.TryGetValue(0, out var vertex) || state.Graphics.Tables.Count != 1 ||
            !state.Graphics.Tables.TryGetValue(0, out var table) || state.Graphics.Views.Count != 0 ||
            (indexedColor
                ? state.Graphics.Constants.Count != 1 || !state.Graphics.Constants.ContainsKey(1)
                : state.Graphics.Constants.Count != 0))
        {
            return RejectRectangleDraw("unsupported rectangle draw or binding shape");
        }

        var stride = vertex.FormatOrStride;

        IndexedUploadPlan? plan = null;
        var vertexRange = (Address: vertex.Address, Bytes: 0);

        if (indexed)
        {
            if (vertices == 0 || vertices % 3 != 0 || state.Index is not { } index ||
                !TryPlanIndexedUpload(vertex, index, vertices, first, baseVertex, out plan))
            {
                return RejectRectangleDraw("invalid or unsupported indexed input assembly extent");
            }
        }
        else if (!TryGetRectangleVertexRange(vertex, first, vertices, out vertexRange))
        {
            return RejectRectangleDraw("invalid non-indexed rectangle vertex range");
        }

        var capacity = plan?.Capacity ?? RectangleVertexOffset + vertexRange.Bytes;

        if (stride is 0 or > 256 || vertex.Size < stride * 3U || vertex.Address == 0 || table == 0 ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2 ||
            state.HostViews.Count > MaximumCommandHostViews - 5)
        {
            return RejectRectangleDraw("vertex extent, descriptor or command-list budget invalid");
        }

        if (!TryResolveClear(self, state.Targets[0], 20, out var color, out var rtv, out var colorFormat, out var target) ||
            !SupportsRectangleTargets(colorFormat, indexedColor, depthHandle.HasValue))
        {
            return RejectRectangleDraw("unsupported color target view");
        }

        var translated = GetRectanglePipeline(state, stride, indexedColor, depthHandle.HasValue, colorFormat);

        if (translated == null)
        {
            return false;
        }

        if (translated.DualMask && indexed)
        {
            return RejectRectangleDraw("sampled mask rectangle requires non-indexed input");
        }

        var constantOffset = 0;

        if (translated.IndexedArray)
        {
            if (plan == null || !indexedColor)
            {
                return RejectRectangleDraw("indexed array pipeline requires indexed triangle input");
            }

            constantOffset = checked((capacity + 255) & ~255);

            capacity = checked(constantOffset + RectangleConstantArrayBytes);

            if (capacity > MaximumIndexedUpload)
            {
                return RejectRectangleDraw("indexed array upload exceeds budget");
            }
        }

        nint depth = 0;
        nuint dsv = 0;

        if (depthHandle is { } boundDepth &&
            (!TryResolveClear(self, boundDepth, 21, out depth, out dsv, out var depthFormat) ||
             depthFormat != 20 || color == depth))
        {
            return RejectRectangleDraw("unsupported depth target view");
        }

        DualMaskRectangleBindings? bindings = null;

        if (translated.DualMask && !TryBindDualMaskTextures(state, table, color, out bindings))
        {
            return false;
        }

        if (!TryReserveRectangleUpload(state, capacity, out var upload))
        {
            return RejectRectangleDraw("could not reserve recorded rectangle upload storage");
        }

        var gpu = upload.GpuAddress;

        var constants = table + (translated.DualMask ? (ulong)DualMaskConstantsOffset : 0UL);
        var deferred = new RectangleUpload(constants, vertexRange.Address, stride, upload)
        {
            Target = target,
            AlphaOnlyTarget = colorFormat == HostTextureViewFormats.A8Unorm,
            Indexed = plan,
            IndexedColor = indexedColor,
            IndexedArray = translated.IndexedArray,
            DualMask = bindings,
            ConstantOffset = constantOffset,
            NonIndexedVertexBytes = vertexRange.Bytes,
            Capacity = capacity,
            ConstantBytes = translated.IndexedArray ? RectangleConstantArrayBytes :
                translated.DualMask ? DualMaskConstantBytes : indexedColor ? 64 : RectangleConstantBytes
        };

        SealMemoryBoundary(self, state, null, null, rectangle: new RectangleDrawBoundary(deferred, false));

        var list = Host(self);
        var commands = (ID3D12GraphicsCommandList*)list;
        var vertexBufferView = new D3D12_VERTEX_BUFFER_VIEW
        {
            BufferLocation = gpu + RectangleVertexOffset,
            SizeInBytes = (uint)(plan?.VertexBytes ?? vertexRange.Bytes),
            StrideInBytes = stride
        };
        var colorView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = rtv };
        var depthView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = dsv };

        TransitionHostResource(list, color, 0, 4);

        if (depth != 0)
        {
            TransitionHostResource(list, depth, 0, 0x10);
        }

        if (bindings is not null)
        {
            TransitionDualMaskTextures(list, bindings, 0, 0x80);

            var heap = (ID3D12DescriptorHeap*)bindings.Heap;
            commands->SetDescriptorHeaps(1, &heap);
        }

        commands->SetPipelineState((ID3D12PipelineState*)translated.Pipeline);

        commands->SetGraphicsRootSignature((ID3D12RootSignature*)translated.Root);

        commands->SetGraphicsRootConstantBufferView(0, gpu + (ulong)constantOffset);

        if (bindings is not null)
        {
            commands->SetGraphicsRootDescriptorTable(1, new D3D12_GPU_DESCRIPTOR_HANDLE { ptr = bindings.GpuTable });
        }

        commands->IASetVertexBuffers(0, 1, &vertexBufferView);

        commands->OMSetRenderTargets(1, &colorView, false, depth != 0 ? &depthView : null);

        if (plan != null)
        {
            var indexBufferView = new D3D12_INDEX_BUFFER_VIEW
            {
                BufferLocation = gpu + (ulong)plan.IndexOffset,
                SizeInBytes = (uint)plan.IndexBytes,
                Format = (DXGI_FORMAT)plan.IndexFormat
            };

            commands->IASetIndexBuffer(&indexBufferView);

            TraceHostCommand(self,
                indexedColor ? "R22 triangle DrawIndexedInstanced" : "R20 rectangle DrawIndexedInstanced");
            // The staged index view starts at the requested first index.
            commands->DrawIndexedInstanced(vertices, instances, 0, baseVertex, firstInstance);
        }
        else
        {
            TraceHostCommand(self, "R14 rectangle DrawInstanced");

            commands->DrawInstanced(vertices, instances, 0, firstInstance);
        }

        commands->OMSetRenderTargets(0, null, false, null);

        if (depth != 0)
        {
            TransitionHostResource(list, depth, 0x10, 0);
        }

        if (bindings is not null)
        {
            TransitionDualMaskTextures(list, bindings, 0x80, 0);
        }

        TransitionHostResource(list, color, 4, 0);

        SealMemoryBoundary(self, state, null, null, rectangle: new RectangleDrawBoundary(deferred, true));

        TraceGraphicsSuccess(indexedColor
            ? $"[graphics] IRX-GFX-INDEX-{(translated.IndexedArray ? "R23" : "R22")}: RECORDED indexed triangle; count={vertices}, first={first}, baseVertex={baseVertex}, awaiting submission."
            : plan == null
                ? "[graphics] IRX-GFX-DRAW-R14: RECORDED guest rectangle; awaiting submission."
                : $"[graphics] IRX-GFX-INDEX-R20: RECORDED indexed rectangle; count={vertices}, first={first}, baseVertex={baseVertex}, awaiting submission.");

        return true;
    }

    private static void ExecuteRectangleDrawBoundary(nint queue, RectangleDrawBoundary boundary,
        OrderedTextureUploads? ordered = null)
    {
        using var stageTiming = BeginTiming(GraphicsStage.ExecuteRectangle);

        var upload = boundary.Upload;

        if (boundary.Complete)
        {
            if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
            {
                Environment.FailFast("[graphics] R14 completion before source preparation.");
            }

            MarkGpuWrite(upload.Target, queue);

            Interlocked.Increment(ref _draws);

            if (upload.DualMask is not null)
            {
                TraceGraphicsSuccess("[graphics] Dual-mask guest rectangle GPU completed.");
            }
            else if (!upload.IndexedColor)
            {
                TraceGraphicsSuccess("[graphics] IRX-GFX-DRAW-R14: GPU COMPLETED guest rectangle (presentation unverified).");
            }

            TraceColor(queue, upload.Target, upload.IndexedArray ? "R23-indexed-output" :
                upload.IndexedColor ? "R22-indexed-output" :
                upload.Indexed == null ? "R14-output" : "R20-indexed-output");

            if (upload.AlphaOnlyTarget)
            {
                TraceGraphicsSuccess("[graphics] Depthless A8 indexed color draw GPU completed.");
            }
            else if (upload.Indexed != null)
            {
                TraceGraphicsSuccess(upload.IndexedColor
                    ? $"[graphics] IRX-GFX-INDEX-{(upload.IndexedArray ? "R23" : "R22")}: GPU COMPLETED indexed triangle (content unverified)."
                    : "[graphics] IRX-GFX-INDEX-R20: GPU COMPLETED indexed rectangle (content unverified).");
            }

            return;
        }

        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0)
        {
            Environment.FailFast("[graphics] R14 experimental draw cannot be resubmitted without reset.");
        }

        if (EnableCompactIndexedUpload && EnableNarrowVertexReads && !EnableColorFlow &&
            upload.IndexedColor && upload.Indexed is { } compactPlan &&
            TryPrepareCompactIndexedColorUpload(upload, compactPlan))
        {
            return;
        }

        Span<byte> token = stackalloc byte[16];
        Span<byte> recheck = stackalloc byte[16];

        var plan = upload.Indexed;
        var capacity = upload.Capacity;
        var rented = capacity is > RectangleStackSnapshotLimit and <= 4 * 1024 * 1024
            ? ArrayPool<byte>.Shared.Rent(capacity)
            : null;

        try
        {
            var snapshot = capacity <= RectangleStackSnapshotLimit
                ? stackalloc byte[capacity]
                : rented == null ? new byte[capacity] : rented.AsSpan(0, capacity);

            snapshot.Clear();

            var vertexBytes = plan?.VertexBytes ?? upload.NonIndexedVertexBytes;

            token.Clear();

            if (!TryReadGuestMemory(upload.Table, token) || BinaryPrimitives.ReadUInt32LittleEndian(token) != ConstantTag)
            {
                Environment.FailFast($"[graphics] R14/R22/R23 submission-time descriptor is not an IRXC CBV token; " +
                                     $"tag=0x{BinaryPrimitives.ReadUInt32LittleEndian(token):X8}.");
            }

            var constants = BinaryPrimitives.ReadUInt64LittleEndian(token[8..]);
            var vertices = snapshot.Slice(RectangleVertexOffset, vertexBytes);
            var readOffset = 0;
            var readBytes = vertexBytes;

            if (plan != null)
            {
                var indices = snapshot.Slice(plan.IndexOffset, plan.IndexBytes);

                if (!TryReadGuestMemory(plan.IndexAddress, indices))
                {
                    Environment.FailFast("[graphics] R26 submission-time index source unreadable.");
                }

                if (!TryGetVertexRange(plan, indices, out var referencedOffset, out var referencedBytes))
                {
                    Environment.FailFast(
                        "[graphics] R26 submission-time index source unreadable or index out of range.");
                }

                // Only the recognized indexed-color shaders use this narrower read.
                // They fetch vertex attributes through IA; the original VB origin,
                // index contents, and base vertex remain unchanged.
                if (EnableNarrowVertexReads && upload.IndexedColor)
                {
                    readOffset = referencedOffset;

                    readBytes = referencedBytes;
                }
            }

            if (!TryReadGuestMemory(upload.Vertex + (ulong)readOffset, vertices.Slice(readOffset, readBytes)))
            {
                Environment.FailFast("[graphics] R26 submission-time vertex source unreadable.");
            }

            var requiredBytes = upload.ConstantBytes;

            if (upload.IndexedArray)
            {
                if (plan == null || !TryConstantExtent(plan, vertices,
                        snapshot.Slice(plan.IndexOffset, plan.IndexBytes),
                        out requiredBytes))
                {
                    Environment.FailFast("[graphics] R23 cannot determine indexed constant extent.");
                }
            }

            var declaredBytes = BinaryPrimitives.ReadUInt32LittleEndian(token[4..]);

            if (constants == 0 || declaredBytes < (uint)requiredBytes ||
                constants > ulong.MaxValue - (ulong)requiredBytes ||
                !TryReadGuestMemory(constants, snapshot.Slice(upload.ConstantOffset, requiredBytes)) ||
                !TryReadGuestMemory(upload.Table, recheck) || !token.SequenceEqual(recheck))
            {
                Environment.FailFast($"[graphics] R14/R22/R23 constant source unreadable or changed; " +
                                     $"declared={declaredBytes}, required={requiredBytes}.");
            }

            if (EnableColorFlow && plan == null && upload.DualMask == null)
            {
                var observation = Interlocked.Increment(ref _rectangleInputReports);

                if (observation <= 4 || (observation <= 64 && (observation & (observation - 1)) == 0))
                {
                    var sampledTexelBlock = snapshot.Slice(32, 16);
                    var multiplier = FormattableString.Invariant(
                        $"({BinaryPrimitives.ReadSingleLittleEndian(sampledTexelBlock):G9}, {BinaryPrimitives.ReadSingleLittleEndian(sampledTexelBlock[4..]):G9}, {BinaryPrimitives.ReadSingleLittleEndian(sampledTexelBlock[8..]):G9}, {BinaryPrimitives.ReadSingleLittleEndian(sampledTexelBlock[12..]):G9})");
                    var sampleBytes = Math.Min(vertexBytes, (int)upload.Stride * 3);

                    Console.Error.WriteLine(
                        $"[graphics] IRX-GFX-INPUT-R21: R14 observation={observation}, CBV multiplier={multiplier}, stride={upload.Stride}, firstRectangle={Convert.ToHexString(vertices[..sampleBytes])}.");
                }
            }

            if (upload.IndexedColor && !upload.IndexedArray && plan != null && EnableColorFlow)
            {
                var observation = Interlocked.Increment(ref _indexedLayoutReports);

                if (observation <= 4 || (observation <= 64 && (observation & (observation - 1)) == 0))
                {
                    var indices = snapshot.Slice(plan.IndexOffset, plan.IndexBytes);
                    var firstIndex = plan.IndexFormat == 57
                        ? BinaryPrimitives.ReadUInt16LittleEndian(indices)
                        : BinaryPrimitives.ReadUInt32LittleEndian(indices);
                    var effective = firstIndex + plan.BaseVertex;
                    var vertexOffset = checked(RectangleVertexOffset + (int)(effective * plan.VertexStride));
                    var firstVertex = snapshot.Slice(vertexOffset, 16);

                    Console.Error.WriteLine($"[graphics] IRX-GFX-INPUT-R22: observation={observation}, " +
                                            $"firstIndex={firstIndex}, effectiveVertex={effective}, firstVertex={Convert.ToHexString(firstVertex)}, "
                                            +
                                            $"CBV64={Convert.ToHexString(snapshot[..64])}.");
                }
            }

            if (upload.IndexedArray && EnableColorFlow)
            {
                var observation = Interlocked.Increment(ref _indexedConstantReports);

                if (observation <= 4 || (observation <= 64 && (observation & (observation - 1)) == 0))
                {
                    Console.Error.WriteLine($"[graphics] IRX-GFX-INPUT-R23: observation={observation}, " +
                                            $"CBV declared={declaredBytes}, referencedExtent={requiredBytes}, blocks={requiredBytes / 64}.");
                }
            }

            var noRead = default(HostRange);

            void* mapped = null;

            var hostUpload = upload.Slice.Host;
            var resultCode = ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(hostUpload, 8))
                (hostUpload, 0, &noRead, &mapped);

            if (resultCode < 0 || mapped == null)
            {
                Environment.FailFast($"[graphics] R14 host upload Map HRESULT=0x{resultCode:X8}.");
            }

            var written = default(HostRange);
            var copiedBytes = capacity;
            var sparse = EnableSparseUpload && EnableNarrowVertexReads &&
                         upload.IndexedColor && plan != null;

            try
            {
                var destination = new Span<byte>((byte*)mapped + upload.Slice.Offset, capacity);

                if (sparse && plan != null)
                {
                    // The recognized indexed-color shaders only read IA attributes
                    // and their CBV. Preserve original GPU addresses and index/base
                    // vertex semantics; publish all potentially addressable CBV bytes.
                    var constantExtent = upload.ConstantBytes;
                    var vertexOffset = checked(RectangleVertexOffset + readOffset);

                    snapshot.Slice(vertexOffset, readBytes).CopyTo(destination.Slice(vertexOffset, readBytes));

                    snapshot.Slice(plan.IndexOffset, plan.IndexBytes)
                        .CopyTo(destination.Slice(plan.IndexOffset, plan.IndexBytes));

                    snapshot.Slice(upload.ConstantOffset, constantExtent)
                        .CopyTo(destination.Slice(upload.ConstantOffset, constantExtent));

                    copiedBytes = checked(readBytes + plan.IndexBytes + constantExtent);
                }
                else
                {
                    snapshot.CopyTo(destination);
                }

                // D3D12 Unmap takes one contiguous written range, so conservatively
                // report the enclosing range even when copies were sparse.
                written.Begin = (nuint)upload.Slice.Offset;
                written.End = written.Begin + (nuint)capacity;
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(hostUpload, 9))
                    (hostUpload, 0, &written);
            }

            if (upload.DualMask is { } dualMask)
            {
                PrepareDualMaskTextures(queue, dualMask, ordered);
            }

            if (sparse)
            {
                var observation = Interlocked.Increment(ref _sparseUploads);

                if (observation <= 4 || (observation & (observation - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[performance] IRX-R29-UPLOAD: observation={observation}; copiedBytes={copiedBytes}; allocatedBytes={capacity}.");
                }
            }

            if (plan != null && upload.IndexedColor && EnableNarrowVertexReads)
            {
                var observation = Interlocked.Increment(ref _vertexReads);

                if (observation <= 4 || (observation & (observation - 1)) == 0)
                {
                    Console.Error.WriteLine(
                        $"[performance] IRX-R26-VERTEX: observation={observation}; guestReadBytes={readBytes}; fullViewBytes={vertexBytes}; offset={readOffset}; hostUploadBytes={capacity}.");
                }
            }

            TraceGraphicsSuccess(
                $"[graphics] IRX-GFX-DRAW-R14: UPLOADED {vertexBytes} vertex bytes and {requiredBytes} CBV bytes.");

            if (plan != null)
            {
                var stage = upload.IndexedArray ? "R23" : upload.IndexedColor ? "R22" : "R20";

                TraceGraphicsSuccess(
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

    private static bool TryConstantExtent(IndexedUploadPlan plan, ReadOnlySpan<byte> vertices,
        ReadOnlySpan<byte> indices, out int bytes, int vertexOffset = 0)
    {
        bytes = 0;

        if (plan.VertexStride != 16 || vertexOffset < 0 || vertexOffset > plan.VertexBytes ||
            vertexOffset % 16 != 0 || vertices.Length > plan.VertexBytes - vertexOffset ||
            !ValidateIndices(plan, indices))
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
            var blueOffset = effective * 16 + 6 - vertexOffset;

            if (blueOffset < 0 || blueOffset >= vertices.Length)
            {
                return false;
            }

            var blue = vertices[(int)blueOffset];

            bytes = Math.Max(bytes, (blue + 1) * 64);
        }

        return bytes is > 0 and <= RectangleConstantArrayBytes;
    }

    private static void VerifyConstantExtent()
    {
        var plan = new IndexedUploadPlan(0x1000, 57, 48, 16, 304, 6, 310, -1);

        Span<byte> vertices = stackalloc byte[48];

        vertices.Clear();

        vertices[6] = 5;

        vertices[22] = 255;

        vertices[38] = 8;

        if (!TryConstantExtent(plan, vertices, [1, 0, 3, 0, 1, 0], out var small) || small != 576 ||
            !TryConstantExtent(plan, vertices, [2, 0, 3, 0, 1, 0], out var large) || large != RectangleConstantArrayBytes ||
            TryConstantExtent(plan, vertices, [0, 0, 1, 0, 2, 0], out _) ||
            !TryConstantExtent(plan, vertices[16..], [2, 0, 3, 0, 2, 0], out var compact, vertexOffset: 16) ||
            compact != RectangleConstantArrayBytes ||
            TryConstantExtent(plan, vertices[16..], [1, 0, 2, 0, 1, 0], out _, vertexOffset: 16))
        {
            throw new InvalidOperationException("R23 indexed constant extent regression failed.");
        }

        Console.WriteLine("[graphics-probe] R23 indexed color constant extent passed (CPU only).");
    }

    private static void VerifyRectangleDepthState()
    {
        var depth = new byte[64];
        var format = new byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(depth, 21);
        BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(format, 16);

        var metadata = new GraphicsPipelineMetadata(0, [], [new(21, depth), new(16, format)], true, true);

        if (!DepthTargetPolicy.TryGetHostState(metadata, false, out var host) ||
            !HasDisabledDepthStencil(host) ||
            BinaryPrimitives.ReadUInt32LittleEndian(host.State[0].Bytes.AsSpan(8)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(depth.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(depth.AsSpan(8)) != 1 ||
            DepthTargetPolicy.TryGetHostState(metadata, true, out _))
        {
            throw new InvalidOperationException("Rectangle depthless pipeline normalization failed.");
        }

        foreach (var unsupportedStateOffset in new[] { 16, 56 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(unsupportedStateOffset), 1);

            if (DepthTargetPolicy.TryGetHostState(metadata, false, out _))
            {
                throw new InvalidOperationException("Rectangle depthless pipeline accepted active stencil or depth bounds.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(depth.AsSpan(unsupportedStateOffset), 0);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(format.AsSpan(4), 20);

        if (!DepthTargetPolicy.TryGetHostState(metadata, true, out host) || !ReferenceEquals(metadata, host) ||
            DepthTargetPolicy.TryGetHostState(metadata, false, out _))
        {
            throw new InvalidOperationException("Rectangle typed depth target was improperly normalized.");
        }

        Console.WriteLine("[graphics-probe] Rectangle depth target and pipeline normalization passed (CPU only).");
    }

    private static void VerifyDepthlessIndexedColorOutput()
    {
        const uint alphaOnly = HostTextureViewFormats.A8Unorm;
        const uint rgba = HostTextureViewFormats.Rgba8Unorm;
        var guest = CreateAlphaVertexFetchProbeState();
        var depth = guest.State.Single(static record => record.Kind == 21).Bytes;
        var targets = guest.State.Single(static record => record.Kind == 15).Bytes;

        if (TryPrepareRectangleHostState(guest, alphaOnly, indexedColor: true, depthBound: false, out _))
        {
            throw new InvalidOperationException("Indexed A8 pipeline accepted active guest depth.");
        }

        depth.AsSpan(4, 8).Clear();

        if (!TryPrepareRectangleHostState(guest, alphaOnly, indexedColor: true, depthBound: false, out var host) ||
            !ReferenceEquals(guest, host) ||
            TryPrepareRectangleHostState(guest, alphaOnly, indexedColor: false, depthBound: false, out _) ||
            TryPrepareRectangleHostState(guest, alphaOnly, indexedColor: true, depthBound: true, out _) ||
            TryPrepareRectangleHostState(guest, rgba, indexedColor: false, depthBound: false, out _))
        {
            throw new InvalidOperationException("Indexed A8 PSO output or depth contract changed.");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(targets.AsSpan(8), rgba);
        BinaryPrimitives.WriteUInt32LittleEndian(targets.AsSpan(36), 2);

        if (TryPrepareRectangleHostState(guest, alphaOnly, indexedColor: true, depthBound: false, out _))
        {
            throw new InvalidOperationException("Indexed A8 pipeline accepted an additional color target.");
        }

        Console.WriteLine("[graphics-probe] Depthless A8 indexed color output policy passed (CPU only).");
    }

    private static void VerifyVertexInputLayout()
    {
        VerifyIndexedUpload();

        VerifyRectangleUploadPlacement();

        VerifyConstantExtent();

        VerifyRectangleDepthState();

        VerifyDepthlessIndexedColorOutput();

        var view = new RecordedBufferView { Address = 0x10000, Size = 192, FormatOrStride = 24 };

        if (!TryGetRectangleVertexRange(view, 2, 6, out var range) || range != (0x10030UL, 144) ||
            TryGetRectangleVertexRange(view, 3, 6, out _) || TryGetRectangleVertexRange(view, 2, 5, out _) ||
            TryGetRectangleVertexRange(view with { Address = (ulong)nint.MaxValue - 40 }, 2, 6, out _))
        {
            throw new InvalidOperationException("Non-indexed rectangle source range or first-vertex rebasing failed.");
        }

        VertexInputElement[] sample =
        [
            new("POSITION", 0, 6, 0, 0, 0, 0),
            new("COLOR", 0, 28, 0, uint.MaxValue, 0, 0),
            new("TEXCOORD", 0, 16, 0, uint.MaxValue, 0, 0)
        ];

        if (sizeof(NativeVertexInputElement) != 32 || !TryNormalizeRectangleLayout(sample, 24, out var resolved) ||
            resolved[1].Offset != 12 || resolved[2].Offset != 16 || TryNormalizeRectangleLayout(sample, 23, out _) ||
            HostRasterTopology(17) != 4 || HostRasterTopology(4) != 4)
        {
            throw new InvalidOperationException("R14 input-layout or topology regression failed.");
        }

        VertexInputElement[] indexedSample =
        [
            new("COLOR", 0, 28, 0, 0, 0, 0),
            new("COLOR", 1, 28, 0, 4, 0, 0),
            new("POSITION", 0, 16, 0, 8, 0, 0)
        ];

        if (!TryNormalizeIndexedRectangleLayout(indexedSample, 16, out var indexedElements) ||
            indexedElements[1].Index != 1 || TryNormalizeIndexedRectangleLayout(indexedSample, 15, out _))
        {
            throw new InvalidOperationException("R22 indexed color input layout regression failed.");
        }

        indexedSample[1] = indexedSample[1] with
        {
            Index = 0
        };

        if (TryNormalizeIndexedRectangleLayout(indexedSample, 16, out _))
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
                var native = new NativeVertexInputElement { Name = (nint)name, Format = 6 };

                if (!TryReadVertexInputLayout((nuint)(&native), 1, out var copied) ||
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
}
