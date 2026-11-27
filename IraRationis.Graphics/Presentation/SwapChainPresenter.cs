// SPDX-License-Identifier: GPL-3.0-or-later
// Presentation follows the title-window / DXGI flip-model architecture in
// DexNom/VIctory, victory-gpu/src/host.rs. This implementation uses an
// exact-format copy on the producing queue, not a CPU readback or plane shader.

using IraRationis.Graphics.Presentation.Models;
using GraphicsDevice = IraRationis.Graphics.ConsoleGraphicsDevice.ConsoleGraphicsDevice;

namespace IraRationis.Graphics.Presentation;

internal sealed unsafe class SwapChainPresenter
{
    private readonly nint _chain;
    private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
    private readonly nint _fence;
    private readonly SwapChainFrameSlot[] _slots = [new(), new(), new()];
    private readonly uint _width, _height, _format;
    private ulong _nextValue;
    private bool _poisoned;

    public SwapChainPresenter(nint window, nint queue, uint width, uint height, uint format)
    {
        Window = window;

        Queue = queue;

        _width = width;

        _height = height;

        _format = format;

        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)GraphicsDevice.Slot(queue, 1))(queue);

        nint factory = 0, baseChain = 0, chain = 0, fence = 0;

        try
        {
            var factoryId = GraphicsDevice.FactoryId;

            GraphicsDevice.CheckHostResult(GraphicsDevice.CreateDxgiFactory(0, &factoryId, &factory),
                "R19 CreateDXGIFactory2");

            var desc = new SwapDescription
            {
                Width = width,
                Height = height,
                Format = format,
                SampleCount = 1,
                BufferUsage = 0x20,
                BufferCount = GraphicsDevice.SwapChainBufferCount,
                Scaling = 0,
                SwapEffect = 4,
                AlphaMode = 3
            };

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, SwapDescription*, void*, nint, nint*, int>)
                    GraphicsDevice.Slot(
                        factory, 15))
                (factory, queue, window, &desc, null, 0, &baseChain), "R19 CreateSwapChainForHwnd");

            var chainId = GraphicsDevice.SwapChainId;

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GraphicsDevice.Slot(baseChain, 0))
                (baseChain, &chainId, &chain), "R19 IDXGISwapChain3");

            _chain = chain;

            var fenceId = GraphicsDevice.FenceId;
            var device = (nint)GraphicsDevice._host;

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)GraphicsDevice.Slot(device, 36))
                (device, 0, 0, &fenceId, &fence), "R19 frame fence");

            _fence = fence;

            for (var index = 0; index < _slots.Length; index++)
            {
                var slot = _slots[index];
                var resourceId = GraphicsDevice.ResourceId;

                nint buffer = 0;

                GraphicsDevice.CheckHostResult(
                    ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)GraphicsDevice.Slot(_chain, 9))
                    (_chain, (uint)index, &resourceId, &buffer), "R19 GetBuffer");

                slot.Buffer = buffer;

                GraphicsDevice.CreateUtilityCommandList(out slot.Allocator, out slot.List);

                GraphicsDevice.CheckHostResult(
                    ((delegate* unmanaged[Stdcall]<nint, int>)GraphicsDevice.Slot(slot.List, 9))(slot.List),
                    "R19 initial close");
            }
        }
        catch
        {
            // No command list has been submitted during construction.
            foreach (var slot in _slots)
            {
                GraphicsDevice.ReleaseUtilityCommands(slot.List, slot.Allocator, slot.Buffer);
            }

            if (_fence != 0)
            {
                _ = GraphicsDevice.HostRelease(_fence);
            }

            if (_chain != 0)
            {
                _ = GraphicsDevice.HostRelease(_chain);
            }

            _ = GraphicsDevice.HostRelease(queue);

            _event.Dispose();

            throw;
        }
        finally
        {
            if (baseChain != 0)
            {
                _ = GraphicsDevice.HostRelease(baseChain);
            }

            if (factory != 0)
            {
                _ = GraphicsDevice.HostRelease(factory);
            }
        }
    }

    public nint Window
    {
        get;
    }

    public nint Queue
    {
        get;
    }

    public bool Matches(uint width, uint height, uint format)
    {
        return width == _width && height == _height && format == _format;
    }

    private void WaitForSlot(SwapChainFrameSlot slot)
    {
        if (slot.FenceValue == 0)
        {
            return;
        }

        var completed = ((delegate* unmanaged[Stdcall]<nint, ulong>)GraphicsDevice.Slot(_fence, 8))(_fence);

        if (completed == ulong.MaxValue)
        {
            throw new InvalidOperationException("R19 GPU removed");
        }

        if (completed < slot.FenceValue)
        {
            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)GraphicsDevice.Slot(_fence, 9))
                (_fence, slot.FenceValue, _event.SafeWaitHandle.DangerousGetHandle()), "R19 frame fence event");

            if (!_event.WaitOne(GraphicsDevice.MemoryWaitTimeoutMilliseconds))
            {
                throw new InvalidOperationException("R19 frame fence timed out");
            }

            completed = ((delegate* unmanaged[Stdcall]<nint, ulong>)GraphicsDevice.Slot(_fence, 8))(_fence);

            if (completed == ulong.MaxValue || completed < slot.FenceValue)
            {
                throw new InvalidOperationException("R19 frame fence completion invalid");
            }
        }

        if (slot.Source != 0)
        {
            _ = GraphicsDevice.HostRelease(slot.Source);

            slot.Source = 0;
        }

        slot.FenceValue = 0;
    }

    public int Present(nint source)
    {
        if (_poisoned)
        {
            return GraphicsDevice.NotImplemented;
        }

        try
        {
            var index = ((delegate* unmanaged[Stdcall]<nint, uint>)GraphicsDevice.Slot(_chain, 36))(_chain);

            if (index >= _slots.Length)
            {
                throw new InvalidOperationException("R19 invalid swap-chain buffer index");
            }

            var slot = _slots[index];

            WaitForSlot(slot);

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, int>)GraphicsDevice.Slot(slot.Allocator, 8))(slot.Allocator),
                "R19 allocator reset");

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)GraphicsDevice.Slot(slot.List, 10))
                (slot.List, slot.Allocator, 0), "R19 list reset");
            // Guest draw paths currently restore COMMON. PRESENT is also 0.
            GraphicsDevice.TransitionHostResource(slot.List, source, 0, GraphicsDevice.CopySource);

            GraphicsDevice.TransitionHostResource(slot.List, slot.Buffer, 0, GraphicsDevice.CopyDestination);

            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)GraphicsDevice.Slot(slot.List, 17))(slot.List,
                slot.Buffer,
                source);

            GraphicsDevice.TransitionHostResource(slot.List, slot.Buffer, GraphicsDevice.CopyDestination, 0);

            GraphicsDevice.TransitionHostResource(slot.List, source, GraphicsDevice.CopySource, 0);

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, int>)GraphicsDevice.Slot(slot.List, 9))(slot.List),
                "R19 list close");
            // Keep the source alive until the copy completes, even if the
            // guest releases its resource immediately after PresentX.
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)GraphicsDevice.Slot(source, 1))(source);

            slot.Source = source;

            var list = slot.List;

            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)GraphicsDevice.Slot(Queue, 10))(Queue, 1, &list);

            var resultCode =
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)GraphicsDevice.Slot(_chain, 8))(_chain, 1, 0);
            var value = ++_nextValue;

            GraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)GraphicsDevice.Slot(Queue, 14))
                (Queue, _fence, value), "R19 frame fence signal");

            slot.FenceValue = value;

            return resultCode;
        }
        catch
        {
            // A submission may be in flight. Do not reset/release unknown
            // resources or reuse this presenter after a synchronization error.
            _poisoned = true;

            throw;
        }
    }
}
