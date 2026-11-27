// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Media;
using IraRationis.Graphics.Textures.Models;
using IraRationis.Shaders.Raster;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using GraphicsDevice = IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Drawing;

/// <summary>
///     Reusable, thread-affine host graphics pipeline for explicitly supplied geometry
///     and RGBA8 textures. Owns the root signature and PSO across render calls.
///     Render returns tightly packed RGBA8 after confirmed GPU completion.
///     Does not infer guest memory ownership or modify guest render targets.
/// </summary>
public sealed unsafe class ProgrammableRasterPipeline : IDisposable
{
    private const int MaximumDimension = 1024;
    private const int MaximumVertices = 3072;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private nint _device, _queue, _root, _pipeline;
    private bool _disposed;

    public ProgrammableRasterPipeline(MediaGpuContext context, ProgrammableRasterProgram program)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(program);

        context.AcquireDeviceQueue(out _device, out _queue);

        try
        {
            if (_device != (nint)GraphicsDevice._host || _queue == 0 || sizeof(RasterVertex) != 48)
            {
                throw new InvalidOperationException(
                    "Raster pipeline requires the runtime D3D12 device and a valid vertex ABI.");
            }

            GraphicsDevice.CreateRasterPipeline(program, out _root, out _pipeline);
        }
        catch
        {
            Dispose();

            throw;
        }
    }

    public void Dispose()
    {
        CheckThread();

        if (_disposed)
        {
            return;
        }

        _disposed = true;

        GraphicsDevice.ReleaseHostObject(_pipeline);
        GraphicsDevice.ReleaseHostObject(_root);
        GraphicsDevice.ReleaseHostObject(_queue);
        GraphicsDevice.ReleaseHostObject(_device);

        _pipeline = 0;
        _root = 0;
        _queue = 0;
        _device = 0;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("Raster pipeline is thread-affine.");
        }
    }

    public byte[] Render(ReadOnlySpan<RasterVertex> vertices, ReadOnlySpan<byte> rgba,
        int textureWidth, int textureHeight, int width, int height)
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_device != (nint)GraphicsDevice._host || vertices.IsEmpty || vertices.Length > MaximumVertices ||
            vertices.Length % 3 != 0 || textureWidth is < 1 or > MaximumDimension ||
            textureHeight is < 1 or > MaximumDimension || width is < 1 or > MaximumDimension ||
            height is < 1 or > MaximumDimension || rgba.Length != checked(textureWidth * textureHeight * 4))
        {
            throw new ArgumentException("Invalid raster draw dimensions, texture or triangle list.");
        }

        foreach (var vertex in vertices)
        {
            if (!ProgrammableRasterProgram.Finite(vertex.Position) ||
                !ProgrammableRasterProgram.Finite(vertex.Color) ||
                !ProgrammableRasterProgram.Finite(vertex.TexCoord))
            {
                throw new ArgumentException("Raster vertices must be finite.", nameof(vertices));
            }
        }

        var vertexBytes = MemoryMarshal.AsBytes(vertices);
        var textureOffset = (vertexBytes.Length + 511) & ~511;
        var texturePitch = (textureWidth * 4 + 255) & ~255;
        var outputPitch = (width * 4 + 255) & ~255;
        var uploadBytes = checked(textureOffset + texturePitch * textureHeight);
        var readbackBytes = checked(outputPitch * height);
        var data = new byte[uploadBytes];

        vertexBytes.CopyTo(data);

        for (var row = 0; row < textureHeight; row++)
        {
            rgba.Slice(row * textureWidth * 4, textureWidth * 4)
                .CopyTo(data.AsSpan(textureOffset + row * texturePitch));
        }

        nint texture = 0, target = 0, readback = 0, rtvHeap = 0;

        try
        {
            GraphicsDevice.CheckHostResult(
                GraphicsDevice.CreateHostResource(GraphicsDevice.RasterTexture(textureWidth, textureHeight, 0), 1, 0,
                    &texture),
                "R67 texture");
            GraphicsDevice.CheckHostResult(
                GraphicsDevice.CreateHostResource(GraphicsDevice.RasterTexture(width, height, 1), 1, 0, &target),
                "R67 target");
            GraphicsDevice.CheckHostResult(GraphicsDevice.CreateHostResource(
                GraphicsDevice.HostBufferDescription((ulong)readbackBytes), 3, GraphicsDevice.CopyDestination,
                &readback), "R67 readback");

            GraphicsDevice.RequireHostObject(texture, "R67 texture");
            GraphicsDevice.RequireHostObject(target, "R67 target");
            GraphicsDevice.RequireHostObject(readback, "R67 readback");

            rtvHeap = GraphicsDevice.CreateShaderViewHeap(_device, 2);

            using var utility = GraphicsDevice.AcquireUtilityContext((ulong)uploadBytes, 1, true);

            utility.PrepareVersionedUpload(data);

            var commands = (ID3D12GraphicsCommandList*)utility.List;
            var heap = (ID3D12DescriptorHeap*)utility.Heap;
            var rtv = ((ID3D12DescriptorHeap*)rtvHeap)->GetCPUDescriptorHandleForHeapStart();
            var srv = heap->GetCPUDescriptorHandleForHeapStart();
            var table = heap->GetGPUDescriptorHandleForHeapStart();

            if (rtv.ptr == 0 || srv.ptr == 0 || table.ptr == 0)
            {
                throw new InvalidOperationException("R67 invalid native descriptor handles.");
            }

            GraphicsDevice._host->CreateRenderTargetView((ID3D12Resource*)target, null, rtv);
            GraphicsDevice._host->CreateShaderResourceView((ID3D12Resource*)texture, null, srv);

            GraphicsDevice.TransitionHostResource(utility.List, texture, 0, GraphicsDevice.CopyDestination);
            GraphicsDevice.RecordHostTextureTransfer(utility.List, texture, utility.Upload, (ulong)uploadBytes,
                new HostTextureTransferFootprint(0, 28, (uint)textureWidth, (uint)textureHeight,
                    (uint)texturePitch, (ulong)textureOffset), true);
            GraphicsDevice.TransitionHostResource(utility.List, texture, GraphicsDevice.CopyDestination, 0x80);
            GraphicsDevice.TransitionHostResource(utility.List, target, 0, 4);

            var clear = stackalloc float[4] { 0, 0, 0, 0 };
            var viewport = new D3D12_VIEWPORT { Width = width, Height = height, MaxDepth = 1.0f };
            var scissor = new RECT { right = width, bottom = height };
            var vertexView = new D3D12_VERTEX_BUFFER_VIEW
            {
                BufferLocation = ((ID3D12Resource*)utility.Upload)->GetGPUVirtualAddress(),
                SizeInBytes = (uint)vertexBytes.Length,
                StrideInBytes = (uint)sizeof(RasterVertex)
            };

            if (vertexView.BufferLocation == 0)
            {
                throw new InvalidOperationException("R67 invalid vertex GPU address.");
            }

            commands->ClearRenderTargetView(rtv, clear, 0, null);
            commands->SetPipelineState((ID3D12PipelineState*)_pipeline);
            commands->SetGraphicsRootSignature((ID3D12RootSignature*)_root);
            commands->SetDescriptorHeaps(1, &heap);
            commands->SetGraphicsRootDescriptorTable(0, table);
            commands->IASetPrimitiveTopology((D3D_PRIMITIVE_TOPOLOGY)4);
            commands->IASetVertexBuffers(0, 1, &vertexView);
            commands->RSSetViewports(1, &viewport);
            commands->RSSetScissorRects(1, &scissor);
            commands->OMSetRenderTargets(1, &rtv, false, null);
            commands->DrawInstanced((uint)vertices.Length, 1, 0, 0);
            commands->OMSetRenderTargets(0, null, false, null);

            GraphicsDevice.TransitionHostResource(utility.List, target, 4, GraphicsDevice.CopySource);
            GraphicsDevice.RecordHostTextureTransfer(utility.List, target, readback, (ulong)readbackBytes,
                new HostTextureTransferFootprint(0, 28, (uint)width, (uint)height, (uint)outputPitch, 0),
                false);
            GraphicsDevice.TransitionHostResource(utility.List, target, GraphicsDevice.CopySource, 0);
            GraphicsDevice.TransitionHostResource(utility.List, texture, 0x80, 0);

            utility.SubmitAndWait(_queue);

            return GraphicsDevice.ReadRasterPixels(readback, width, height, outputPitch);
        }
        finally
        {
            GraphicsDevice.ReleaseHostObject(rtvHeap);
            GraphicsDevice.ReleaseHostObject(readback);
            GraphicsDevice.ReleaseHostObject(target);
            GraphicsDevice.ReleaseHostObject(texture);
        }
    }
}
