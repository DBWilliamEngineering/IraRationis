// SPDX-License-Identifier: GPL-3.0-or-later
// Presentation follows the title-window / DXGI flip-model architecture in
// DexNom/VIctory, victory-gpu/src/host.rs. This implementation uses an
// exact-format copy on the producing queue, not a CPU readback or plane shader.

using IraRationis.Graphics.Presentation.Models;

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

        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)ConsoleGraphicsDevice.Slot(queue, 1))(queue);

        nint factory = 0, baseChain = 0, chain = 0, fence = 0;

        try
        {
            var factoryId = ConsoleGraphicsDevice.FactoryId;

            ConsoleGraphicsDevice.CheckHostResult(ConsoleGraphicsDevice.CreateDxgiFactory(0, &factoryId, &factory),
                "R19 CreateDXGIFactory2");

            var desc = new SwapDescription
            {
                Width = width,
                Height = height,
                Format = format,
                SampleCount = 1,
                BufferUsage = 0x20,
                BufferCount = ConsoleGraphicsDevice.SwapChainBufferCount,
                Scaling = 0,
                SwapEffect = 4,
                AlphaMode = 3
            };

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, SwapDescription*, void*, nint, nint*, int>)
                    ConsoleGraphicsDevice.Slot(
                        factory, 15))
                (factory, queue, window, &desc, null, 0, &baseChain), "R19 CreateSwapChainForHwnd");

            var chainId = ConsoleGraphicsDevice.SwapChainId;

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(baseChain, 0))
                (baseChain, &chainId, &chain), "R19 IDXGISwapChain3");

            _chain = chain;

            var fenceId = ConsoleGraphicsDevice.FenceId;
            var device = (nint)ConsoleGraphicsDevice._host;

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(device,
                    36))
                (device, 0, 0, &fenceId, &fence), "R19 frame fence");

            _fence = fence;

            for (var index = 0; index < _slots.Length; index++)
            {
                var slot = _slots[index];
                var resourceId = ConsoleGraphicsDevice.ResourceId;

                nint buffer = 0;

                ConsoleGraphicsDevice.CheckHostResult(
                    ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(_chain, 9))
                    (_chain, (uint)index, &resourceId, &buffer), "R19 GetBuffer");

                slot.Buffer = buffer;

                ConsoleGraphicsDevice.CreateUtilityCommandList(out slot.Allocator, out slot.List);

                ConsoleGraphicsDevice.CheckHostResult(
                    ((delegate* unmanaged[Stdcall]<nint, int>)ConsoleGraphicsDevice.Slot(slot.List, 9))(slot.List),
                    "R19 initial close");
            }
        }
        catch
        {
            // No command list has been submitted during construction.
            foreach (var slot in _slots)
            {
                ConsoleGraphicsDevice.ReleaseUtilityCommands(slot.List, slot.Allocator, slot.Buffer);
            }

            if (_fence != 0)
            {
                _ = ConsoleGraphicsDevice.HostRelease(_fence);
            }

            if (_chain != 0)
            {
                _ = ConsoleGraphicsDevice.HostRelease(_chain);
            }

            _ = ConsoleGraphicsDevice.HostRelease(queue);

            _event.Dispose();

            throw;
        }
        finally
        {
            if (baseChain != 0)
            {
                _ = ConsoleGraphicsDevice.HostRelease(baseChain);
            }

            if (factory != 0)
            {
                _ = ConsoleGraphicsDevice.HostRelease(factory);
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

        var completed = ((delegate* unmanaged[Stdcall]<nint, ulong>)ConsoleGraphicsDevice.Slot(_fence, 8))(_fence);

        if (completed == ulong.MaxValue)
        {
            throw new InvalidOperationException("R19 GPU removed");
        }

        if (completed < slot.FenceValue)
        {
            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)ConsoleGraphicsDevice.Slot(_fence, 9))
                (_fence, slot.FenceValue, _event.SafeWaitHandle.DangerousGetHandle()), "R19 frame fence event");

            if (!_event.WaitOne(ConsoleGraphicsDevice.MemoryWaitTimeoutMilliseconds))
            {
                throw new InvalidOperationException("R19 frame fence timed out");
            }

            completed = ((delegate* unmanaged[Stdcall]<nint, ulong>)ConsoleGraphicsDevice.Slot(_fence, 8))(_fence);

            if (completed == ulong.MaxValue || completed < slot.FenceValue)
            {
                throw new InvalidOperationException("R19 frame fence completion invalid");
            }
        }

        if (slot.Source != 0)
        {
            _ = ConsoleGraphicsDevice.HostRelease(slot.Source);

            slot.Source = 0;
        }

        slot.FenceValue = 0;
    }

    public int Present(nint source)
    {
        if (_poisoned)
        {
            return ConsoleGraphicsDevice.NotImplemented;
        }

        try
        {
            var index = ((delegate* unmanaged[Stdcall]<nint, uint>)ConsoleGraphicsDevice.Slot(_chain, 36))(_chain);

            if (index >= _slots.Length)
            {
                throw new InvalidOperationException("R19 invalid swap-chain buffer index");
            }

            var slot = _slots[index];

            WaitForSlot(slot);

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, int>)ConsoleGraphicsDevice.Slot(slot.Allocator,
                    8))(slot.Allocator),
                "R19 allocator reset");

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)ConsoleGraphicsDevice.Slot(slot.List, 10))
                (slot.List, slot.Allocator, 0), "R19 list reset");
            // Guest draw paths currently restore COMMON. PRESENT is also 0.
            ConsoleGraphicsDevice.TransitionHostResource(slot.List, source, 0, ConsoleGraphicsDevice.CopySource);

            ConsoleGraphicsDevice.TransitionHostResource(slot.List, slot.Buffer, 0,
                ConsoleGraphicsDevice.CopyDestination);

            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)ConsoleGraphicsDevice.Slot(slot.List, 17))(slot.List,
                slot.Buffer,
                source);

            ConsoleGraphicsDevice.TransitionHostResource(slot.List, slot.Buffer, ConsoleGraphicsDevice.CopyDestination,
                0);

            ConsoleGraphicsDevice.TransitionHostResource(slot.List, source, ConsoleGraphicsDevice.CopySource, 0);

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, int>)ConsoleGraphicsDevice.Slot(slot.List, 9))(slot.List),
                "R19 list close");
            // Keep the source alive until the copy completes, even if the
            // guest releases its resource immediately after PresentX.
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)ConsoleGraphicsDevice.Slot(source, 1))(source);

            slot.Source = source;

            var list = slot.List;

            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)ConsoleGraphicsDevice.Slot(Queue, 10))(Queue, 1,
                &list);

            var resultCode =
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)ConsoleGraphicsDevice.Slot(_chain, 8))(_chain, 1,
                    0);
            var value = ++_nextValue;

            ConsoleGraphicsDevice.CheckHostResult(
                ((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)ConsoleGraphicsDevice.Slot(Queue, 14))
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
