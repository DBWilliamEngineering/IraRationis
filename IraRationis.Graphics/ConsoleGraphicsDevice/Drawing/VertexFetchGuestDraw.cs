// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Drawing;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Shaders.VertexFetch;
using IraRationis.Shaders.VertexFetch.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int VertexFetchUploadCapacity = 65536;
    private const int MaximumCommandHostViews = 16384;
    // Experimental CPU-authoritative source path, not a general VUM implementation.
    // Guest synchronization must protect the source address and its contents until execution.
    private static readonly bool ExecuteGuestVertexFetch =
        Environment.GetEnvironmentVariable("IRX_EXECUTE_GUEST_DRAW") != "0";

    private static int _vertexFetchExecutionReported;

    // The command state owns all COM references in HostViews until normal list reset/release.
    // Cache entries only borrow those references; weak keys cannot prolong command state lifetime.
    private static readonly ConditionalWeakTable<RecordedCommandState, Dictionary<nint, VertexFetchPipeline>>
        VertexFetchPipelines = new();

    private static readonly NativePipelineCache<GraphicsPipelineMetadata, VertexFetchPipeline>
        SharedVertexFetchPipelines =
            new("vertex fetch", static value => (value.Root, value.Pipeline));
    private static readonly NativePipelineCache<GraphicsPipelineMetadata, RectanglePipeline> SharedRectanglePipelines =
        new("rectangle", static value => (value.Root, value.Pipeline));
    private static readonly NativePipelineCache<GraphicsPipelineMetadata, RectanglePipeline> SharedTexturedPipelines =
        new("textured rectangle", static value => (value.Root, value.Pipeline));
    private static readonly NativePipelineCache<GuestComputePipelineKey, ColorLutPipeline> SharedColorLutPipelines =
        new("compute", static value => (value.Root, value.Pipeline));

    internal static void RetainHostObject(nint value)
    {
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 1))(value);
    }

    private static bool TryRecordVertexFetchGuestDraw(nint self, uint vertices, uint instances, uint first,
        uint firstInstance)
    {
        using var stageTiming = BeginTiming(GraphicsStage.RecordVertexFetch);

        if (Volatile.Read(ref _vertexFetchExecutionReported) == 0 &&
            Interlocked.Exchange(ref _vertexFetchExecutionReported, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-DRAW-R13.1: guest execution enabled={ExecuteGuestVertexFetch}; " +
                "set IRX_EXECUTE_GUEST_DRAW=0 to disable this experimental path.");
        }

        if (!ExecuteGuestVertexFetch)
        {
            return RejectVertexFetchDraw("guest execution explicitly disabled by IRX_EXECUTE_GUEST_DRAW=0");
        }

        var state = StateFor(self);

        if (_host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0)
        {
            return RejectVertexFetchDraw("host direct command list unavailable");
        }

        // Admit complete triangle lists; the existing submission-time extent check
        // still enforces the fixed upload capacity for every referenced vertex.
        if (vertices == 0 || vertices > 1023 || vertices % 3 != 0 ||
            instances != 1 || first != 0 || firstInstance != 0)
        {
            return RejectVertexFetchDraw($"unsupported draw arguments: vertices={vertices}, instances={instances}, " +
                                         $"first={first}, firstInstance={firstInstance}");
        }

        if (state.Topology != 4 || state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 })
        {
            return RejectVertexFetchDraw($"unsupported raster state: topology={state.Topology}, " +
                                         $"viewports={state.Viewports?.Length ?? 0}, scissors={state.Scissors?.Length ?? 0}");
        }

        if (state.Targets.Length != 1)
        {
            return RejectVertexFetchDraw($"expected a single render target, found {state.Targets.Length}");
        }

        if (state.Graphics.Tables.Count != 0 || state.Graphics.Constants.Count != 0 ||
            state.Graphics.Views.Count != 1 || !state.Graphics.Views.TryGetValue(0, out var source))
        {
            return RejectVertexFetchDraw($"unsupported root bindings: tables={state.Graphics.Tables.Count}, " +
                                         $"constants={state.Graphics.Constants.Count}, views={state.Graphics.Views.Count}; "
                                         +
                                         "expected only root SRV slot zero");
        }

        if (source.Kind != 1 || source.Address == 0 || (source.Address & 3UL) != 0)
        {
            return RejectVertexFetchDraw($"unsupported root SRV: kind={source.Kind}, address=0x{source.Address:X}");
        }

        if (state.MemoryBoundaries.Count > MaximumMemoryOperations - 2 ||
            state.HostViews.Count > MaximumCommandHostViews - 5 ||
            state.UploadBytes > UploadBudget - VertexFetchUploadCapacity)
        {
            return RejectVertexFetchDraw($"command-list budget exceeded: boundaries={state.MemoryBoundaries.Count}, " +
                                         $"hostReferences={state.HostViews.Count}, uploadBytes={state.UploadBytes}");
        }

        VertexFetchPipeline? translated;

        nint upload = 0;

        try
        {
            translated = GetVertexFetchPipeline(state);

            if (translated == null)
            {
                return RejectVertexFetchDraw("no supported translated native PSO");
            }

            if (translated.DepthBoundsEnabled && state.DepthBounds is { } bounds &&
                (bounds.Min != 0.0f || bounds.Max != 1.0f))
            {
                return RejectVertexFetchDraw("nondefault dynamic depth bounds are not yet replayed");
            }

            if (!TryReadShaderHeaderLength(translated.Pair, out _))
            {
                return RejectVertexFetchDraw("unsupported vertex header layout");
            }

            if (!TryResolveClear(self, state.Targets[0], 20, out var color, out var rtv, out var colorFormat,
                    out var guestTarget, true))
            {
                return RejectVertexFetchDraw($"RTV resolution failed: handle=0x{state.Targets[0]:X}");
            }

            if (colorFormat != translated.ColorFormat)
            {
                return RejectVertexFetchDraw(
                    $"RTV format {colorFormat} does not match PSO format {translated.ColorFormat}");
            }

            nint depth = 0;
            nuint dsv = 0;

            if (state.Depth is { } depthHandle)
            {
                if (!TryResolveClear(self, depthHandle, 21, out depth, out dsv, out var depthFormat))
                {
                    return RejectVertexFetchDraw($"DSV resolution failed: handle=0x{depthHandle:X}");
                }

                if (depthFormat != 20 || color == depth)
                {
                    return RejectVertexFetchDraw(
                        $"unsupported depth target: format={depthFormat}, aliasesColor={color == depth}");
                }
            }

            // The validated shader fetches from the root SRV using SV_VertexID.
            // Unused guest IA vertex and index views have no effect on this draw.

            // Capacity is fixed for this diagnostic slice; no guest payload is read during recording.
            var resultCode =
                CreateHostResource(HostBufferDescription(VertexFetchUploadCapacity), 2, GenericRead, &upload);

            if (resultCode < 0 || upload == 0)
            {
                return RejectVertexFetchDraw($"host upload creation failed: 0x{resultCode:X8}");
            }

            var gpuAddress = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();

            if (gpuAddress == 0 || (gpuAddress & 3UL) != 0)
            {
                return RejectVertexFetchDraw("host upload GPU virtual address is invalid");
            }

            var deferred = new VertexFetchVertexUpload(translated.Pair, source.Address, first, vertices, upload)
            {
                AlphaOnlyTarget = colorFormat == HostTextureViewFormats.A8Unorm,
                Target = guestTarget
            };

            state.HostViews.Add(upload);

            upload = 0;

            state.UploadBytes += VertexFetchUploadCapacity;

            // No recoverable fallback after a segment has been sealed: doing so would leave a
            // partly recorded draw in the list. The existing segment helper fail-fasts on failure.
            SealMemoryBoundary(self, state, null, null, draw: new VertexFetchDrawBoundary(deferred, false));

            var list = Host(self);
            var commands = (ID3D12GraphicsCommandList*)list;
            var colorView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = rtv };
            var depthView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = dsv };

            TransitionHostResource(list, color, 0, 4);

            if (depth != 0)
            {
                TransitionHostResource(list, depth, 0, 0x10);
            }

            commands->SetPipelineState((ID3D12PipelineState*)translated.Pipeline);

            commands->SetGraphicsRootSignature((ID3D12RootSignature*)translated.Root);

            commands->SetGraphicsRootShaderResourceView(0, gpuAddress);
            // SealMemoryBoundary already replayed topology, viewports, scissors, blend and stencil.
            commands->OMSetRenderTargets(1, &colorView, false, depth == 0 ? null : &depthView);

            RecordHostGpuCommand(self, "R13 DrawInstanced");

            commands->DrawInstanced(vertices, instances, first, firstInstance);

            commands->OMSetRenderTargets(0, null, false, null);

            if (depth != 0)
            {
                TransitionHostResource(list, depth, 0x10, 0);
            }

            TransitionHostResource(list, color, 4, 0);

            // A second boundary proves that the submitted draw has completed before we report success.
            // This is deliberately synchronous validation, not the production queue scheduling design.
            SealMemoryBoundary(self, state, null, null, draw: new VertexFetchDrawBoundary(deferred, true));

            TraceGraphicsSuccess($"[graphics] IRX-GFX-DRAW-R13: RECORDED guest draw; " +
                                 $"source=0x{source.Address:X}, vertices={vertices}, colorFormat={colorFormat}, " +
                                 $"depthBound={depth != 0}, boundaries={state.MemoryBoundaries.Count}; awaiting submission.");

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

    private static bool RejectVertexFetchDraw(string reason)
    {
        Console.Error.WriteLine($"[graphics] IRX-GFX-DRAW-R13: REFUSED: {reason}.");

        return false;
    }

    private static VertexFetchPipeline? GetVertexFetchPipeline(RecordedCommandState state)
    {
        if (!IsRootSignature(state.Graphics.Signature) || state.Pipeline == 0)
        {
            return null;
        }

        var cache = VertexFetchPipelines.GetOrCreateValue(state);
        var depthBound = state.Depth.HasValue;

        if (cache.TryGetValue(state.Pipeline, out var cached))
        {
            // Even on cache hits, require the current guest root to match the retained pipeline.
            lock (PipelineGate)
            {
                return cached.DepthBound == depthBound &&
                       GraphicsPipelines.TryGetValue(state.Pipeline, out var metadata) &&
                       metadata.Root == state.Graphics.Signature
                    ? cached
                    : null;
            }
        }

        GraphicsPipelineMetadata? pipeline;

        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out pipeline);
        }

        if (pipeline == null || pipeline.Root != state.Graphics.Signature || pipeline.Shaders.Count != 2 ||
            !TryPrepareVertexFetchHostState(pipeline, depthBound, out var hostState, out var colorFormat))
        {
            return null;
        }

        var vertex = pipeline.Shaders.FirstOrDefault(static shader => shader.Stage == 1);
        var pixel = pipeline.Shaders.FirstOrDefault(static shader => shader.Stage == 2);
        var depth = hostState.State.FirstOrDefault(static record => record.Kind == 21);

        if (vertex == null || pixel == null || depth == null || depth.Bytes.Length != 64)
        {
            return null;
        }

        // The candidate builder rejects duplicate/unsupported stream records. Nondefault
        // dynamic bounds are refused at each draw, including when the PSO is cached.
        var depthBoundsEnabled = BinaryPrimitives.ReadUInt32LittleEndian(depth.Bytes.AsSpan(56)) != 0;

        if (SharedVertexFetchPipelines.Acquire(pipeline, state) is { } shared)
        {
            cache.Add(state.Pipeline, shared);

            return shared;
        }

        var root = (void*)null;

        nint nativePipeline = 0;

        try
        {
            var pair = Rdna2VertexFetchCompiler.Translate(vertex.Container.Bytes, pixel.Container.Bytes,
                RootSignatureBlob(pipeline.Root).ToArray());
            var report = Console.Error;
            var vertexShader = CompileHostShader(pair.VertexSource, "vs_5_1", report);
            var pixelShader = CompileHostShader(pair.PixelSource, "ps_5_1", report);
            var rootBlob = SerializeHostRoot(pair, report);

            if (vertexShader == null || pixelShader == null || rootBlob == null)
            {
                return null;
            }

            var iid = RootSignatureId;

            fixed (byte* bytes = rootBlob)
            {
                var resultCode = (int)_host->CreateRootSignature(0, bytes, (nuint)rootBlob.Length, &iid, &root);

                if (resultCode < 0 || root == null)
                {
                    _ = RejectVertexFetchDraw($"native root creation failed: 0x{resultCode:X8}");

                    return null;
                }
            }

            nativePipeline = CreatePipelineCandidate(root, vertexShader, pixelShader, hostState, report,
                allowDepthless: !depthBound, colorFormat: colorFormat);

            if (nativePipeline == 0)
            {
                return null;
            }

            var result = new VertexFetchPipeline(pair, (nint)root, nativePipeline, colorFormat, depthBound,
                depthBoundsEnabled);

            state.HostViews.Add((nint)root);

            root = null;

            state.HostViews.Add(nativePipeline);

            nativePipeline = 0;

            cache.Add(state.Pipeline, result);

            SharedVertexFetchPipelines.Publish(pipeline, result);

            report.WriteLine(
                "[graphics] IRX-GFX-DRAW-R13: retained translated root and PSO for command-list lifetime.");

            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                              NotSupportedException or OverflowException or DllNotFoundException or
                                              EntryPointNotFoundException)
        {
            _ = RejectVertexFetchDraw($"pipeline translation: {exception.Message}");

            return null;
        }
        finally
        {
            if (nativePipeline != 0)
            {
                _ = HostRelease(nativePipeline);
            }

            if (root != null)
            {
                _ = HostRelease((nint)root);
            }
        }
    }

    private static void VerifyVertexFetchBoundaryRecording(nint list, RecordedCommandState state)
    {
        var pair = new VertexFetchShaderPair(string.Empty, string.Empty, 0, 3,
            new VertexFetchField(0, 3), new VertexFetchField(12, 4));
        var upload = new VertexFetchVertexUpload(pair, 0x10000, 0, 3, 0);
        var before = state.MemoryBoundaries.Count;

        SealMemoryBoundary(list, state, null, null, draw: new VertexFetchDrawBoundary(upload, false));

        SealMemoryBoundary(list, state, null, null, draw: new VertexFetchDrawBoundary(upload, true));

        var prepareWithWait = new MemoryBoundary(0, null, new MemoryWait(0x10000, 3, 0, 4), null,
            new VertexFetchDrawBoundary(upload, false), null, null, null);

        if (state.MemoryBoundaries.Count != before + 2 || upload.Phase != 0 || prepareWithWait.IsDrawPreparation ||
            state.MemoryBoundaries[before].Draw is not { Complete: false } prepare ||
            state.MemoryBoundaries[before + 1].Draw is not { Complete: true } complete ||
            !state.MemoryBoundaries[before].IsDrawPreparation || state.MemoryBoundaries[before + 1].IsDrawPreparation ||
            !ReferenceEquals(prepare.Upload, upload) || !ReferenceEquals(complete.Upload, upload))
        {
            throw new InvalidOperationException("R13 deferred draw boundary recording failed.");
        }

        Console.WriteLine("[graphics-probe] R13 draw preparation/completion boundary ordering passed (CPU only).");
    }

    private static void ExecuteVertexFetchDrawBoundary(nint queue, VertexFetchDrawBoundary boundary)
    {
        using var stageTiming = BeginTiming(GraphicsStage.ExecuteVertexFetch);

        var upload = boundary.Upload;

        if (boundary.Complete)
        {
            if (Interlocked.CompareExchange(ref upload.Phase, 2, 1) != 1)
            {
                Environment.FailFast("[graphics] R13 draw completion arrived before source preparation.");
            }

            MarkGpuWrite(upload.Target, queue);

            Interlocked.Increment(ref _draws);

            if (upload.AlphaOnlyTarget)
            {
                TraceGraphicsSuccess("[graphics] Depthless A8 guest triangle GPU completed.");
            }
            else
            {
                TraceGraphicsSuccess("[graphics] IRX-GFX-DRAW-R13: GPU COMPLETED guest draw " +
                                     "(CPU-authoritative source assumption; presentation not verified).");
            }

            TraceColor(queue, upload.Target, "R13-output");

            return;
        }

        // Each recorded diagnostic draw is single-use. A normal reset creates a new allocation.
        // Reject resubmission instead of mutating storage that another execution could reference.
        if (Interlocked.CompareExchange(ref upload.Phase, 1, 0) != 0)
        {
            Environment.FailFast("[graphics] R13 experimental draw cannot be resubmitted without list reset.");
        }

        if (!TryReadShaderHeaderLength(upload.Pair, out var headerLength))
        {
            Environment.FailFast("[graphics] R13 invalid vertex-fetch header layout.");
        }

        Span<byte> header = stackalloc byte[headerLength];

        ulong extent = 0;

        if (!TryReadGuestMemory(upload.Address, header) ||
            !Rdna2VertexFetchCompiler.TryGetReadExtent(upload.Pair, header, upload.First, upload.Count,
                VertexFetchUploadCapacity, out extent) || extent < (ulong)headerLength)
        {
            Environment.FailFast("[graphics] R13 submission-time vertex-fetch header or extent refused.");
        }

        var bytes = checked((int)extent);

        byte[]? rented = null;

        var snapshot = bytes <= 256
            ? stackalloc byte[bytes]
            : (rented = ArrayPool<byte>.Shared.Rent(bytes)).AsSpan(0, bytes);

        try
        {
            if (!TryReadGuestMemory(upload.Address, snapshot) || !snapshot[..headerLength].SequenceEqual(header))
            {
                Environment.FailFast("[graphics] R13 submission-time source unreadable or header changed.");
            }

            // Copy the validated CPU snapshot once into write-combined memory; never read back from the UPLOAD heap.
            var noRead = default(HostRange);

            void* mapped = null;

            var resultCode = ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload.Host, 8))
                (upload.Host, 0, &noRead, &mapped);

            if (resultCode < 0 || mapped == null)
            {
                Environment.FailFast($"[graphics] R13 upload map failed: 0x{resultCode:X8}.");
            }

            var written = default(HostRange);

            try
            {
                snapshot.CopyTo(new Span<byte>(mapped, bytes));

                written.End = (nuint)bytes;
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload.Host, 9))
                    (upload.Host, 0, &written);
            }
        }
        finally
        {
            if (rented != null)
            {
                ArrayPool<byte>.Shared.Return(rented, true);
            }
        }

        TraceGraphicsSuccess(
            $"[graphics] IRX-GFX-DRAW-R13: UPLOADED {bytes} guest bytes after preceding GPU boundaries.");
    }
}
