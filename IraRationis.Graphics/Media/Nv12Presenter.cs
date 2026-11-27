// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Media.Models;
using IraRationis.Graphics.Presentation.Models;
using IraRationis.Graphics.Resources.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.Media;

/// <summary>Thread-affine, bounded host NV12 presenter. No guest resource state is published.</summary>
public sealed unsafe class Nv12Presenter : IDisposable
{
    private const int BufferCount = 3;
    private readonly EventWaitHandle _completion = new(false, EventResetMode.AutoReset);
    private readonly Nv12Frame[] _frames = [new(), new(), new()];
    private readonly int _height;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly D3D12_PLACED_SUBRESOURCE_FOOTPRINT[] _planes = new D3D12_PLACED_SUBRESOURCE_FOOTPRINT[2];
    private readonly TextWriter _report;
    private readonly MediaGpuContext? _sharedContext;
    private readonly int _width;
    private ulong _colorBytes, _nextFence;
    private D3D12_PLACED_SUBRESOURCE_FOOTPRINT _colorLayout;
    private ulong _copyBytes;
    private nint _copyQueue, _copyFence;
    private long _copySubmissions, _copyWaits;
    private nint _device, _queue, _fence, _root, _pipeline, _chain;
    private bool _disposed;
    private long _submitted, _presented, _waitCount, _waitTicks, _submitTicks;
    private int _uploadBytes;

    public Nv12Presenter(nint window, int width, int height, TextWriter report,
        MediaGpuContext? sharedContext = null)
    {
        _sharedContext = sharedContext;

        _width = width;

        _height = height;

        _report = report;

        try
        {
            ArgumentNullException.ThrowIfNull(report);

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("D3D12 requires Windows.");
            }

            if (width is < 2 or > 3840 || height is < 2 or > 2160 || (width & 1) != 0 || (height & 1) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "Even positive NV12 dimensions required.");
            }

            if (window == 0 || ConsoleGraphicsDevice.IsPresentationWindow(window) == 0
                            || ConsoleGraphicsDevice.GetWindowThreadProcessId(window, out var process) == 0
                            ||
                            process != (uint)Environment.ProcessId)
            {
                throw new ArgumentException("An owned host window is required.", nameof(window));
            }

            Initialize(window);
        }
        catch
        {
            ReleaseObjects();

            _completion.Dispose();

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

        Wait(_nextFence);

        try
        {
            var average = _submitted == 0 ? 0 : _submitTicks * 1000.0 / Stopwatch.Frequency / _submitted;
            var wait = _waitTicks * 1000.0 / Stopwatch.Frequency;

            _report.WriteLine($"[media] IRX-R43: submitted={_submitted}; presented={_presented}; " +
                              $"fence={_nextFence}; blockingWaits={_waitCount}; waitMs={wait:F3}; " +
                              $"meanSubmitWallMs={average:F3}; readbackBytes=0.");

            _report.WriteLine($"[media] IRX-R49: copySubmissions={_copySubmissions}; gpuQueueWaits={_copyWaits}; " +
                              $"activePixelBytes={_copyBytes}; copyCpuWaits=0.");
        }
        finally
        {
            ReleaseObjects();

            _completion.Dispose();
        }
    }

    private void Initialize(nint window)
    {
        if (sizeof(ResourceDescription) != sizeof(D3D12_RESOURCE_DESC) ||
            sizeof(TextureCopyLocation) != sizeof(D3D12_TEXTURE_COPY_LOCATION) ||
            sizeof(VideoPlaneView) != sizeof(D3D12_SHADER_RESOURCE_VIEW_DESC))
        {
            throw new InvalidOperationException("Unexpected D3D12 interop structure size.");
        }

        if (_sharedContext is not null)
        {
            _sharedContext.AcquireDeviceQueue(out _device, out _queue);
        }
        else
        {
            var deviceId = ConsoleGraphicsDevice.DeviceId;
            var device = nint.Zero;
            var resultCode = ConsoleGraphicsDevice.D3D12CreateDevice(0, 0xC000, &deviceId, &device);

            _device = device;

            ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 device");

            ConsoleGraphicsDevice.RequireHostObject(_device, "device");

            var queueDescription = default(D3D12_COMMAND_QUEUE_DESC);
            var queueId = ConsoleGraphicsDevice.QueueId;
            var queue = nint.Zero;
            var createQueue =
                (delegate* unmanaged[Stdcall]<nint, D3D12_COMMAND_QUEUE_DESC*, Guid*, nint*, int>)ConsoleGraphicsDevice
                    .Slot(_device, 8);

            resultCode = createQueue(_device, &queueDescription, &queueId, &queue);

            _queue = queue;

            ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 queue");

            ConsoleGraphicsDevice.RequireHostObject(_queue, "queue");
        }

        var nativeDevice = (ID3D12Device*)_device;
        var fenceId = ConsoleGraphicsDevice.FenceId;
        var fence = nint.Zero;
        var createFence =
            (delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(_device, 36);
        var fenceResult = createFence(_device, 0, 0, &fenceId, &fence);

        _fence = fence;

        ConsoleGraphicsDevice.CheckHostResult(fenceResult, "R43 fence");

        ConsoleGraphicsDevice.RequireHostObject(_fence, "fence");

        if (Environment.GetEnvironmentVariable("IRX_HOST_MEDIA_COPY_QUEUE") == "1")
        {
            InitializeCopyQueue();
        }

        var description = new ResourceDescription
        {
            Dimension = 3,
            Width = (ulong)_width,
            Height = (uint)_height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 103,
            SampleCount = 1
        };

        var layouts = stackalloc D3D12_PLACED_SUBRESOURCE_FOOTPRINT[2];
        var rows = stackalloc uint[2];
        var rowBytes = stackalloc ulong[2];
        var total = 0UL;

        nativeDevice->GetCopyableFootprints((D3D12_RESOURCE_DESC*)&description, 0, 2, 0, layouts, rows, rowBytes,
            &total);

        var yEnd = ConsoleGraphicsDevice.ValidateTextureFootprint(layouts[0], rows[0], rowBytes[0], _width, _height);
        var uvEnd = ConsoleGraphicsDevice.ValidateTextureFootprint(layouts[1], rows[1], rowBytes[1], _width,
            _height / 2);
        var upload = Math.Max(total, Math.Max(yEnd, uvEnd));

        if (layouts[0].Offset != 0 || layouts[1].Offset < yEnd || upload > 64UL * 1024 * 1024)
        {
            throw new InvalidDataException("R43 invalid plane footprints.");
        }

        _planes[0] = layouts[0];

        _planes[1] = layouts[1];

        _uploadBytes = checked((int)upload);

        // Query the output footprint as well. Shader writes directly into this pitched GPU buffer.
        var color = description;

        color.Format = 87; // B8G8R8A8_UNORM
        var colorLayout = default(D3D12_PLACED_SUBRESOURCE_FOOTPRINT);
        var colorRows = 0U;
        var colorRowBytes = 0UL;
        var colorTotal = 0UL;

        nativeDevice->GetCopyableFootprints((D3D12_RESOURCE_DESC*)&color, 0, 1, 0,
            &colorLayout, &colorRows, &colorRowBytes, &colorTotal);

        var colorEnd =
            ConsoleGraphicsDevice.ValidateTextureFootprint(colorLayout, colorRows, colorRowBytes, checked(_width * 4),
                _height);

        _colorLayout = colorLayout;

        _colorBytes = Math.Max(colorTotal, colorEnd);

        if (_colorLayout.Offset != 0 || _colorBytes > 64UL * 1024 * 1024)
        {
            throw new InvalidDataException("R43 invalid BGRA footprint.");
        }

        var shader = ConsoleGraphicsDevice.Nv12ConversionShader.Replace("(id.y * width + id.x) * 4",
            FormattableString.Invariant($"id.y * {_colorLayout.Footprint.RowPitch}u + id.x * 4u"),
            StringComparison.Ordinal);

        if (_sharedContext is not null)
        {
            _sharedContext.AcquirePipeline(shader, out _root, out _pipeline);
        }
        else
        {
            ConsoleGraphicsDevice.CreateNv12ConversionPipeline(_device, _report, out _root, out _pipeline, shader);
        }

        if (_sharedContext is not null)
        {
            _report.WriteLine("[media] IRX-R44: presenter uses shared host device, queue and pipeline cache.");
        }

        CreateSwapchain(window);

        for (var index = 0; index < BufferCount; index++)
        {
            InitializeFrame(_frames[index], (uint)index, description);
        }

        _report.WriteLine($"[media] IRX-R49: copyQueue={_copyQueue != 0}; directQueue=1; slots={BufferCount}.");

        _report.WriteLine($"[media] IRX-R43: persistent device=1; queue=1; pipeline=1; slots={BufferCount}; " +
                          $"size={_width}x{_height}; uploadBytesPerSlot={_uploadBytes}; colorBytesPerSlot={_colorBytes}; "
                          +
                          $"colorPitch={_colorLayout.Footprint.RowPitch}; readback=False.");
    }

    private void InitializeCopyQueue()
    {
        var description = default(D3D12_COMMAND_QUEUE_DESC);

        description.Type = (D3D12_COMMAND_LIST_TYPE)3; // COPY
        var queueId = ConsoleGraphicsDevice.QueueId;
        var queue = nint.Zero;
        var createQueue =
            (delegate* unmanaged[Stdcall]<nint, D3D12_COMMAND_QUEUE_DESC*, Guid*, nint*, int>)
            ConsoleGraphicsDevice.Slot(_device, 8);
        var resultCode = createQueue(_device, &description, &queueId, &queue);

        _copyQueue = queue;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R49 copy queue");

        ConsoleGraphicsDevice.RequireHostObject(_copyQueue, "copy queue");

        var fenceId = ConsoleGraphicsDevice.FenceId;
        var fence = nint.Zero;
        var createFence =
            (delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(_device, 36);

        resultCode = createFence(_device, 0, 0, &fenceId, &fence);

        _copyFence = fence;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R49 copy fence");

        ConsoleGraphicsDevice.RequireHostObject(_copyFence, "copy fence");
    }

    private void InitializeCopyFrame(Nv12Frame frame)
    {
        var allocator = nint.Zero;
        var allocatorId = ConsoleGraphicsDevice.AllocatorId;
        var createAllocator =
            (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(_device, 9);
        var resultCode = createAllocator(_device, 3, &allocatorId, &allocator);

        frame.CopyAllocator = allocator;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R49 copy allocator");

        ConsoleGraphicsDevice.RequireHostObject(frame.CopyAllocator, "copy allocator");

        var list = nint.Zero;
        var listId = ConsoleGraphicsDevice.HostListId;
        var createList =
            (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(
                _device, 12);

        resultCode = createList(_device, 0, 3, frame.CopyAllocator, 0, &listId, &list);

        frame.CopyList = list;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R49 copy list");

        ConsoleGraphicsDevice.RequireHostObject(frame.CopyList, "copy list");

        ConsoleGraphicsDevice.CheckHostResult(
            ((delegate* unmanaged[Stdcall]<nint, int>)ConsoleGraphicsDevice.Slot(frame.CopyList, 9))(frame.CopyList),
            "R49 initial copy close");
    }

    private void RecordPlaneCopies(Nv12Frame frame, ID3D12GraphicsCommandList* commands)
    {
        for (var plane = 0; plane < 2; plane++)
        {
            var source = new TextureCopyLocation { Resource = frame.Upload, Type = 1, Footprint = _planes[plane] };
            var destination = new TextureCopyLocation { Resource = frame.Texture, Subresource = (uint)plane };

            commands->CopyTextureRegion((D3D12_TEXTURE_COPY_LOCATION*)&destination, 0, 0, 0,
                (D3D12_TEXTURE_COPY_LOCATION*)&source, null);
        }
    }

    private void RecordCopyFrame(Nv12Frame frame)
    {
        var commands = (ID3D12GraphicsCommandList*)frame.CopyList;

        ConsoleGraphicsDevice.CheckHostResult(((ID3D12CommandAllocator*)frame.CopyAllocator)->Reset(),
            "R49 copy allocator reset");

        ConsoleGraphicsDevice.CheckHostResult(commands->Reset((ID3D12CommandAllocator*)frame.CopyAllocator, null),
            "R49 copy list reset");
        // COMMON -> COPY_DEST implicit promotion; COPY execution decays to COMMON.
        RecordPlaneCopies(frame, commands);

        ConsoleGraphicsDevice.CheckHostResult(commands->Close(), "R49 copy close");
    }

    private void SubmitCopyFrame(Nv12Frame frame, ulong value)
    {
        // Both lists have been closed successfully before either is submitted.
        // If a post-submit synchronization fails, returning would allow unsafe reuse.
        try
        {
            var list = frame.CopyList;
            var execute =
                (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)ConsoleGraphicsDevice.Slot(_copyQueue, 10);
            var signal =
                (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)ConsoleGraphicsDevice.Slot(_copyQueue, 14);
            var wait = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)ConsoleGraphicsDevice.Slot(_queue, 15);

            execute(_copyQueue, 1, &list);

            ConsoleGraphicsDevice.CheckHostResult(signal(_copyQueue, _copyFence, value), "R49 copy signal");

            _copySubmissions++;

            _copyBytes += (ulong)_width * (ulong)_height * 3 / 2;

            // GPU-side wait: the CPU does not block for the copy fence.
            ConsoleGraphicsDevice.CheckHostResult(wait(_queue, _copyFence, value), "R49 direct queue wait");

            _copyWaits++;
        }
        catch (Exception exception)
        {
            Environment.FailFast("R49 copy submission could not establish safe queue ordering.", exception);
        }
    }

    private void CreateSwapchain(nint window)
    {
        var factory = nint.Zero;
        var baseChain = nint.Zero;
        var chain = nint.Zero;

        try
        {
            var factoryId = ConsoleGraphicsDevice.FactoryId;

            ConsoleGraphicsDevice.CheckHostResult(ConsoleGraphicsDevice.CreateDxgiFactory(0, &factoryId, &factory),
                "R43 DXGI factory");

            ConsoleGraphicsDevice.RequireHostObject(factory, "DXGI factory");

            var description = new SwapDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = 87,
                SampleCount = 1,
                BufferUsage = 0x20,
                BufferCount = BufferCount,
                Scaling = 0,
                SwapEffect = 4,
                AlphaMode = 3
            };
            var create =
                (delegate* unmanaged[Stdcall]<nint, nint, nint, SwapDescription*, void*, nint, nint*, int>)
                ConsoleGraphicsDevice.Slot(
                    factory, 15);

            ConsoleGraphicsDevice.CheckHostResult(create(factory, _queue, window, &description, null, 0, &baseChain),
                "R43 swapchain");

            ConsoleGraphicsDevice.RequireHostObject(baseChain, "swapchain");

            var id = ConsoleGraphicsDevice.SwapChainId;
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(baseChain, 0);
            var resultCode = query(baseChain, &id, &chain);

            _chain = chain;

            ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 IDXGISwapChain3");

            ConsoleGraphicsDevice.RequireHostObject(_chain, "IDXGISwapChain3");
        }
        finally
        {
            ConsoleGraphicsDevice.ReleaseHostObject(baseChain);

            ConsoleGraphicsDevice.ReleaseHostObject(factory);
        }
    }

    private void InitializeFrame(Nv12Frame frame, uint index, ResourceDescription description)
    {
        frame.Texture = ConsoleGraphicsDevice.CreateHostTransferResource(_device, description, 1,
            _copyQueue == 0 ? ConsoleGraphicsDevice.CopyDestination : 0);

        if (_copyQueue != 0)
        {
            InitializeCopyFrame(frame);
        }

        frame.Upload = ConsoleGraphicsDevice.CreateHostTransferResource(_device,
            ConsoleGraphicsDevice.HostBufferDescription((ulong)_uploadBytes), 2, ConsoleGraphicsDevice.GenericRead);
        // Upload heaps may remain mapped for their entire lifetime. Each slot
        // is written only after its previous GPU submission has completed.
        var range = default(HostRange);
        var mapped = (void*)null;
        var map =
            (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)ConsoleGraphicsDevice.Slot(frame.Upload,
                8);

        ConsoleGraphicsDevice.CheckHostResult(map(frame.Upload, 0, &range, &mapped), "R46 persistent upload map");

        frame.UploadMapped = true;

        frame.UploadAddress = (nint)mapped;

        if (mapped == null)
        {
            throw new InvalidOperationException("R46 upload map returned null.");
        }

        new Span<byte>(mapped, _uploadBytes).Clear();

        var outputDescription = ConsoleGraphicsDevice.HostBufferDescription(_colorBytes);

        outputDescription.Flags = 4;

        frame.Output = ConsoleGraphicsDevice.CreateHostTransferResource(_device, outputDescription, 1, 8);

        var resourceId = ConsoleGraphicsDevice.ResourceId;
        var buffer = nint.Zero;
        var getBuffer =
            (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(_chain, 9);
        var resultCode = getBuffer(_chain, index, &resourceId, &buffer);

        frame.Buffer = buffer;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 back buffer");

        ConsoleGraphicsDevice.RequireHostObject(frame.Buffer, "back buffer");

        var heapDescription = new DescriptorHeapDescription { Kind = 0, Count = 2, Flags = 1 };
        var heapId = ConsoleGraphicsDevice.DescriptorHeapId;
        var heap = nint.Zero;
        var createHeap =
            (delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)ConsoleGraphicsDevice
                .Slot(_device, 14);

        resultCode = createHeap(_device, &heapDescription, &heapId, &heap);

        frame.Heap = heap;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 SRV heap");

        ConsoleGraphicsDevice.RequireHostObject(frame.Heap, "SRV heap");

        var nativeDevice = (ID3D12Device*)_device;
        var nativeHeap = (ID3D12DescriptorHeap*)frame.Heap;
        var cpu = nativeHeap->GetCPUDescriptorHandleForHeapStart();
        var gpu = nativeHeap->GetGPUDescriptorHandleForHeapStart();
        var increment = nativeDevice->GetDescriptorHandleIncrementSize(0);

        if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0)
        {
            throw new InvalidOperationException("R43 invalid descriptor handles.");
        }

        var yView = new VideoPlaneView { Format = 61, Dimension = 4, Mapping = 5768, MipLevels = 1 };
        var uvView = new VideoPlaneView { Format = 49, Dimension = 4, Mapping = 5768, MipLevels = 1, PlaneSlice = 1 };

        nativeDevice->CreateShaderResourceView((ID3D12Resource*)frame.Texture,
            (D3D12_SHADER_RESOURCE_VIEW_DESC*)&yView, cpu);

        cpu.ptr += increment;

        nativeDevice->CreateShaderResourceView((ID3D12Resource*)frame.Texture,
            (D3D12_SHADER_RESOURCE_VIEW_DESC*)&uvView, cpu);

        var allocator = nint.Zero;
        var allocatorId = ConsoleGraphicsDevice.AllocatorId;
        var createAllocator =
            (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(_device, 9);

        resultCode = createAllocator(_device, 0, &allocatorId, &allocator);

        frame.Allocator = allocator;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 allocator");

        ConsoleGraphicsDevice.RequireHostObject(frame.Allocator, "allocator");

        var list = nint.Zero;
        var listId = ConsoleGraphicsDevice.HostListId;
        var createList =
            (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(
                _device, 12);

        resultCode = createList(_device, 0, 0, frame.Allocator, 0, &listId, &list);

        frame.List = list;

        ConsoleGraphicsDevice.CheckHostResult(resultCode, "R43 list");

        ConsoleGraphicsDevice.RequireHostObject(frame.List, "list");

        ConsoleGraphicsDevice.CheckHostResult(
            ((delegate* unmanaged[Stdcall]<nint, int>)ConsoleGraphicsDevice.Slot(frame.List, 9))(frame.List),
            "R43 initial close");
    }

    /// <summary>Submit one packed NV12 frame. True means DXGI accepted a non-occluded present.</summary>
    public bool Present(ReadOnlySpan<byte> nv12)
    {
        CheckThread();

        ObjectDisposedException.ThrowIf(_disposed, this);

        var yBytes = checked(_width * _height);

        if (nv12.Length != yBytes + yBytes / 2)
        {
            throw new ArgumentException("Unexpected NV12 frame size.", nameof(nv12));
        }

        var started = Stopwatch.GetTimestamp();
        var index = ((delegate* unmanaged[Stdcall]<nint, uint>)ConsoleGraphicsDevice.Slot(_chain, 36))(_chain);

        if (index >= BufferCount)
        {
            throw new InvalidOperationException("R43 invalid back-buffer index.");
        }

        var frame = _frames[index];

        Wait(frame.FenceValue);

        var lumaRows = _height;
        var chromaRows = _height / 2;

        var luma = nv12[..yBytes];
        var chroma = nv12[yBytes..];

        // The initial mapping cleared all padding. Subsequent writes touch
        // only active pixels, never reading write-combined upload memory.
        var upload = new Span<byte>((void*)frame.UploadAddress, _uploadBytes);

        ConsoleGraphicsDevice.CopyVideoRows(luma, _width, upload, _planes[0], _width, lumaRows);

        ConsoleGraphicsDevice.CopyVideoRows(chroma, _width, upload, _planes[1], _width, chromaRows);

        var commands = (ID3D12GraphicsCommandList*)frame.List;

        ConsoleGraphicsDevice.CheckHostResult(((ID3D12CommandAllocator*)frame.Allocator)->Reset(),
            "R43 allocator reset");

        ConsoleGraphicsDevice.CheckHostResult(commands->Reset((ID3D12CommandAllocator*)frame.Allocator, null),
            "R43 command reset");

        if (_copyQueue == 0)
        {
            RecordPlaneCopies(frame, commands);
        }
        else
        {
            RecordCopyFrame(frame);
        }

        ConsoleGraphicsDevice.TransitionHostResource(frame.List, frame.Texture,
            _copyQueue == 0 ? ConsoleGraphicsDevice.CopyDestination : 0, 0x40);

        var heap = (ID3D12DescriptorHeap*)frame.Heap;

        commands->SetDescriptorHeaps(1, &heap);

        commands->SetPipelineState((ID3D12PipelineState*)_pipeline);

        commands->SetComputeRootSignature((ID3D12RootSignature*)_root);

        commands->SetComputeRootDescriptorTable(0, heap->GetGPUDescriptorHandleForHeapStart());

        var address = ((ID3D12Resource*)frame.Output)->GetGPUVirtualAddress();

        if (address == 0 || (address & 3UL) != 0)
        {
            throw new InvalidOperationException("R43 invalid UAV address.");
        }

        commands->SetComputeRootUnorderedAccessView(1, address);

        commands->Dispatch((uint)((_width + 7) / 8), (uint)((_height + 7) / 8), 1);

        ConsoleGraphicsDevice.TransitionHostResource(frame.List, frame.Output, 8, ConsoleGraphicsDevice.CopySource);

        ConsoleGraphicsDevice.TransitionHostResource(frame.List, frame.Buffer, 0,
            ConsoleGraphicsDevice.CopyDestination);

        var output = new TextureCopyLocation { Resource = frame.Output, Type = 1, Footprint = _colorLayout };
        var target = new TextureCopyLocation { Resource = frame.Buffer };

        commands->CopyTextureRegion((D3D12_TEXTURE_COPY_LOCATION*)&target, 0, 0, 0,
            (D3D12_TEXTURE_COPY_LOCATION*)&output, null);

        ConsoleGraphicsDevice.TransitionHostResource(frame.List, frame.Buffer, ConsoleGraphicsDevice.CopyDestination,
            0);

        ConsoleGraphicsDevice.TransitionHostResource(frame.List, frame.Output, ConsoleGraphicsDevice.CopySource, 8);

        ConsoleGraphicsDevice.TransitionHostResource(frame.List, frame.Texture, 0x40,
            _copyQueue == 0 ? ConsoleGraphicsDevice.CopyDestination : 0);

        ConsoleGraphicsDevice.CheckHostResult(commands->Close(), "R43 close");

        if (_nextFence >= ulong.MaxValue - 1)
        {
            throw new InvalidOperationException("R43 fence timeline exhausted.");
        }

        var list = frame.List;
        var value = _nextFence + 1;
        var execute = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)ConsoleGraphicsDevice.Slot(_queue, 10);
        var present = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)ConsoleGraphicsDevice.Slot(_chain, 8);
        var signal = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)ConsoleGraphicsDevice.Slot(_queue, 14);

        if (_copyQueue != 0)
        {
            SubmitCopyFrame(frame, value);
        }

        execute(_queue, 1, &list);

        var result = 0;

        try
        {
            result = present(_chain, 1, 0);
        }
        finally
        {
            // No allocator, upload heap or descriptor can be reused until this signal completes.
            var resultCode = signal(_queue, _fence, value);

            if (resultCode < 0)
            {
                Environment.FailFast($"R43 signal failed after submission: 0x{resultCode:X8}.");
            }

            _nextFence = value;

            frame.FenceValue = value;

            _submitted++;
        }

        ConsoleGraphicsDevice.CheckHostResult(result, "R43 present");

        _submitTicks += Stopwatch.GetTimestamp() - started;

        if (result == 0)
        {
            _presented++;

            return true;
        }

        return false;
    }

    private void Wait(ulong value)
    {
        if (value == 0)
        {
            return;
        }

        // Any failure to prove completion is fatal; never release live GPU objects on timeout.
        try
        {
            var getCompleted = (delegate* unmanaged[Stdcall]<nint, ulong>)ConsoleGraphicsDevice.Slot(_fence, 8);
            var completed = getCompleted(_fence);

            if (completed == ulong.MaxValue)
            {
                Environment.FailFast("R43 device removed.");
            }

            if (completed >= value)
            {
                return;
            }

            var start = Stopwatch.GetTimestamp();

            _completion.Reset();

            var setEvent = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)ConsoleGraphicsDevice.Slot(_fence, 9);
            var resultCode = setEvent(_fence, value, _completion.SafeWaitHandle.DangerousGetHandle());

            if (resultCode < 0 || !_completion.WaitOne(TimeSpan.FromSeconds(15)))
            {
                Environment.FailFast($"R43 completion wait failed: 0x{resultCode:X8}.");
            }

            completed = getCompleted(_fence);

            if (completed == ulong.MaxValue || completed < value)
            {
                Environment.FailFast("R43 invalid GPU completion.");
            }

            _waitCount++;

            _waitTicks += Stopwatch.GetTimestamp() - start;
        }
        catch (Exception exception)
        {
            Environment.FailFast("R43 completion could not be proved.", exception);
        }
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("R43 presenter is thread-affine.");
        }
    }

    private void ReleaseObjects()
    {
        foreach (var frame in _frames)
        {
            ConsoleGraphicsDevice.ReleaseHostObject(frame.CopyList);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.CopyAllocator);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.List);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.Allocator);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.Heap);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.Buffer);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.Output);

            if (frame.UploadMapped)
            {
                var range = new HostRange { End = (nuint)_uploadBytes };
                var unmap =
                    (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)ConsoleGraphicsDevice.Slot(frame.Upload,
                        9);

                unmap(frame.Upload, 0, &range);

                frame.UploadMapped = false;

                frame.UploadAddress = 0;
            }

            ConsoleGraphicsDevice.ReleaseHostObject(frame.Upload);

            ConsoleGraphicsDevice.ReleaseHostObject(frame.Texture);
        }

        ConsoleGraphicsDevice.ReleaseHostObject(_chain);

        ConsoleGraphicsDevice.ReleaseHostObject(_pipeline);

        ConsoleGraphicsDevice.ReleaseHostObject(_root);

        ConsoleGraphicsDevice.ReleaseHostObject(_copyFence);

        ConsoleGraphicsDevice.ReleaseHostObject(_copyQueue);

        ConsoleGraphicsDevice.ReleaseHostObject(_fence);

        ConsoleGraphicsDevice.ReleaseHostObject(_queue);

        ConsoleGraphicsDevice.ReleaseHostObject(_device);
    }
}
