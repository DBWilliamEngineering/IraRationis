// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Resources;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static bool TryRecordPlanarColorGuestDraw(nint self, uint vertices, uint instances,
        uint first, uint firstInstance)
    {
        using var stageTiming = BeginTiming(GraphicsStage.RecordTexture);

        var state = StateFor(self);

        GraphicsPipelineMetadata? metadata;

        lock (PipelineGate)
        {
            _ = GraphicsPipelines.TryGetValue(state.Pipeline, out metadata);
        }

        if (metadata == null)
        {
            return false;
        }

        var srgbTexture = IsSrgbTextureStrip(metadata);

        if (!ExecuteGuestVertexFetch || _host == null || Host(self) == 0 || *(uint*)(self + 0x10) != 0 ||
            vertices != 4 || instances != 1 || firstInstance != 0 || state.Topology != TriangleStripTopology ||
            state.Viewports is not { Length: 1 } || state.Scissors is not { Length: 1 } || state.Targets.Length != 1 ||
            (state.Depth.HasValue && !srgbTexture) || state.Vertices.Count != 1 || !state.Vertices.TryGetValue(0, out var vertex) ||
            state.Graphics.Tables.Count != 1 || !state.Graphics.Tables.TryGetValue(0, out var table) ||
            state.Graphics.Views.Count != 0 || state.Graphics.Constants.Count != 0)
        {
            return RejectPlanarColorDraw("requires a supported four-vertex triangle strip, one vertex stream/table, and compatible depth state");
        }

        var start = (ulong)first * vertex.FormatOrStride;
        var vertexBytes = (ulong)vertices * vertex.FormatOrStride;
        var capacity = vertexBytes + PlanarColorVertexOffset;

        if (vertex.FormatOrStride != 24 || vertex.Address == 0 || vertex.Address > (ulong)nint.MaxValue ||
            start > vertex.Size || vertexBytes > vertex.Size - start ||
            start > (ulong)nint.MaxValue - vertex.Address ||
            vertexBytes > (ulong)nint.MaxValue - vertex.Address - start || table == 0 ||
            table > (ulong)nint.MaxValue - 0xD0 ||
            state.MemoryBoundaries.Count > MaximumMemoryOperations - 2 ||
            state.HostViews.Count > MaximumCommandHostViews - 5 || state.UploadBytes > UploadBudget - capacity)
        {
            return RejectPlanarColorDraw("vertex extent, descriptor table, or command budget invalid");
        }

        var pipeline = GetPlanarColorPipeline(state, metadata, srgbTexture);

        if (pipeline == null)
        {
            return RejectPlanarColorDraw("verified host shader pipeline unavailable");
        }

        if (!TryGetPlanarColorSources(state, table, srgbTexture, out var transform, out var textures) || transform == null)
        {
            return false;
        }

        if (!TryResolveClear(self, state.Targets[0], 20, out var color, out var renderTargetView, out var colorFormat,
                reportFailure: true))
        {
            return RejectPlanarColorDraw("could not resolve a compatible native render target view");
        }

        // CreatePipelineCandidate accepts exactly one UNORM RTV for this PSO.
        // Keep the typed view check; the backing allocation may be typeless.
        if (colorFormat != HostTextureViewFormats.Rgba8Unorm)
        {
            return RejectPlanarColorDraw($"RTV format {colorFormat} does not match the UNORM pipeline");
        }

        if (textures.Any(binding => binding.Host == color))
        {
            return RejectPlanarColorDraw("a sampled texture aliases the host render target");
        }

        var target = FindOwnedResource(state, color);

        if (target == 0)
        {
            return RejectPlanarColorDraw("render target was not retained");
        }

        var targetDescription = ((ResourceObject*)target)->Description;

        TraceGraphicsSuccess($"[graphics] planar-color target RESOLVED: guestFormat={targetDescription.Format}; " +
            $"hostFormat={HostTextureViewFormats.BackingFormat(targetDescription)}; viewFormat={colorFormat}.");

        return RecordPlanarColorBindings(self, state, pipeline, transform, textures, table,
            vertex.Address + start, (int)vertexBytes, (int)capacity, color, renderTargetView, target);
    }

    private static bool RecordPlanarColorBindings(nint self, RecordedCommandState state, PlanarColorPipeline pipeline,
        TextureSource transform, PlanarColorTextureBinding[] textures, ulong table, ulong vertex,
        int vertexBytes, int capacity, nint color, nuint renderTargetView, nint target)
    {
        nint upload = 0;
        nint heap = 0;

        try
        {
            var heapDescription = new DescriptorHeapDescription { Kind = 0, Count = 4, Flags = 1 };
            var heapId = DescriptorHeapId;
            var hostDevice = (nint)_host;
            var resultCode =
                ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(hostDevice, 14))
                (hostDevice, &heapDescription, &heapId, &heap);

            if (resultCode < 0 || heap == 0)
            {
                return RejectPlanarColorDraw($"shader-visible descriptor heap HRESULT=0x{resultCode:X8}");
            }

            WritePlanarColorViews(heap, textures, pipeline.SrgbTexture);

            resultCode = CreateHostResource(HostBufferDescription((ulong)capacity), 2, GenericRead, &upload);

            if (resultCode < 0 || upload == 0)
            {
                return RejectPlanarColorDraw($"vertex and constant upload HRESULT=0x{resultCode:X8}");
            }

            var gpuAddress = ((ID3D12Resource*)upload)->GetGPUVirtualAddress();
            var descriptors = ((ID3D12DescriptorHeap*)heap)->GetGPUDescriptorHandleForHeapStart();

            if (gpuAddress == 0 || (gpuAddress & 255UL) != 0 || descriptors.ptr == 0)
            {
                return RejectPlanarColorDraw("invalid host upload or descriptor address");
            }

            var deferred = new PlanarColorDrawUpload(transform, textures, table, vertex, vertexBytes, capacity,
                upload, target, ((ResourceObject*)target)->Generation, pipeline.SrgbTexture);

            state.HostViews.Add(upload);

            upload = 0;

            state.HostViews.Add(heap);

            var nativeHeap = heap;

            heap = 0;

            state.UploadBytes += (ulong)capacity;

            SealMemoryBoundary(self, state, null, null, planarColor: new PlanarColorDrawBoundary(deferred, false));

            RecordPlanarColorCommands(self, pipeline, textures, nativeHeap, gpuAddress, descriptors,
                (uint)vertexBytes, color, renderTargetView);

            SealMemoryBoundary(self, state, null, null, planarColor: new PlanarColorDrawBoundary(deferred, true));

            if (pipeline.SrgbTexture)
            {
                TraceGraphicsSuccess("[graphics] srgb-texture-strip RECORDED: vertices=4; textures=1; depthInactive=True; awaiting submission.");
            }
            else
            {
                TraceGraphicsSuccess($"[graphics] planar-color RECORDED: vertices=4; planes=3; lookupView={textures.Length == 4}; awaiting submission.");
            }

            return true;
        }
        finally
        {
            if (upload != 0)
            {
                _ = HostRelease(upload);
            }

            if (heap != 0)
            {
                _ = HostRelease(heap);
            }
        }
    }

    private static void RecordPlanarColorCommands(nint self, PlanarColorPipeline pipeline,
        PlanarColorTextureBinding[] textures, nint heap, ulong gpuAddress,
        D3D12_GPU_DESCRIPTOR_HANDLE descriptors, uint vertexBytes, nint color, nuint renderTargetView)
    {
        var list = Host(self);
        var commands = (ID3D12GraphicsCommandList*)list;
        var nativeHeap = (ID3D12DescriptorHeap*)heap;
        var colorView = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = renderTargetView };
        var vertexView = new D3D12_VERTEX_BUFFER_VIEW
        {
            BufferLocation = gpuAddress + PlanarColorVertexOffset,
            SizeInBytes = vertexBytes,
            StrideInBytes = 24
        };

        TransitionHostResource(list, color, 0, 4);

        TransitionPlanarColorTextures(list, textures, 0, 0x80);

        commands->SetDescriptorHeaps(1, &nativeHeap);
        commands->SetPipelineState((ID3D12PipelineState*)pipeline.Pipeline);
        commands->SetGraphicsRootSignature((ID3D12RootSignature*)pipeline.Root);
        commands->SetGraphicsRootConstantBufferView(0, gpuAddress);
        commands->SetGraphicsRootConstantBufferView(1, gpuAddress + 256);
        commands->SetGraphicsRootConstantBufferView(2, gpuAddress + 512);
        commands->SetGraphicsRootDescriptorTable(3, descriptors);
        commands->IASetVertexBuffers(0, 1, &vertexView);
        commands->OMSetRenderTargets(1, &colorView, false, null);

        TraceHostCommand(self, "planar-color triangle-strip DrawInstanced");

        commands->DrawInstanced(4, 1, 0, 0);
        commands->OMSetRenderTargets(0, null, false, null);

        TransitionPlanarColorTextures(list, textures, 0x80, 0);

        TransitionHostResource(list, color, 4, 0);
    }
}
