// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Descriptors.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Media.Models;
using IraRationis.Graphics.Resources.Models;
using IraRationis.Graphics.Textures;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    internal const string Nv12ConversionShader = """
                                                Texture2D<float> Luma : register(t0);
                                                Texture2D<float2> Chroma : register(t1);
                                                RWByteAddressBuffer Output : register(u0);

                                                [numthreads(8, 8, 1)]
                                                void main(uint3 id : SV_DispatchThreadID)
                                                {
                                                    uint width, height;
                                                    Luma.GetDimensions(width, height);

                                                    if (id.x >= width || id.y >= height)
                                                    {
                                                        return;
                                                    }

                                                    int c = (int)round(Luma.Load(int3(id.xy, 0)) * 255.0) - 16;
                                                    int2 uv = (int2)round(Chroma.Load(int3(id.xy / 2, 0)) * 255.0) - 128;

                                                    uint b = (uint)clamp((298 * c + 541 * uv.x + 128) >> 8, 0, 255);
                                                    uint g = (uint)clamp((298 * c - 55 * uv.x - 136 * uv.y + 128) >> 8, 0, 255);
                                                    uint r = (uint)clamp((298 * c + 459 * uv.y + 128) >> 8, 0, 255);

                                                    Output.Store((id.y * width + id.x) * 4, b | (g << 8) | (r << 16) | 0xFF000000u);
                                                }
                                                """;

    /// <summary>
    ///     Converts independently supplied, limited-range BT.709 NV12 using a real two-plane
    ///     D3D12 texture and a compute shader. This synchronous host diagnostic owns a separate
    ///     device and queue; it does not publish guest resource state or use a SwapChain.
    /// </summary>
    public static void ConvertNv12FrameOnGpu(ReadOnlySpan<byte> luma, int lumaStride,
        ReadOnlySpan<byte> chroma, int chromaStride, int width, int height, Span<byte> bgra,
        TextWriter report)
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

        ValidateVideoPlane(luma.Length, lumaStride, width, height);

        ValidateVideoPlane(chroma.Length, chromaStride, width, height / 2);

        var outputBytes = checked(width * height * 4);

        if (bgra.Length < outputBytes || luma.Overlaps(bgra) || chroma.Overlaps(bgra))
        {
            throw new ArgumentException("Distinct, sufficiently sized BGRA output required.", nameof(bgra));
        }

        // These explicit-layout interop structs describe PC D3D12, not the guest ABI.
        if (sizeof(ResourceDescription) != sizeof(D3D12_RESOURCE_DESC) ||
            sizeof(TextureCopyLocation) != sizeof(D3D12_TEXTURE_COPY_LOCATION) ||
            sizeof(VideoPlaneView) != sizeof(D3D12_SHADER_RESOURCE_VIEW_DESC))
        {
            throw new InvalidOperationException("Unexpected D3D12 interop structure size.");
        }

        var device = nint.Zero;
        var queue = nint.Zero;
        var fence = nint.Zero;
        var allocator = nint.Zero;
        var list = nint.Zero;
        var texture = nint.Zero;
        var upload = nint.Zero;
        var output = nint.Zero;
        var readback = nint.Zero;
        var root = nint.Zero;
        var pipeline = nint.Zero;
        var heap = nint.Zero;

        using var completion = new EventWaitHandle(false, EventResetMode.AutoReset);

        try
        {
            var deviceId = DeviceId;

            CheckHostResult(D3D12CreateDevice(0, 0xC000, &deviceId, &device), "R42 D3D12CreateDevice");

            RequireHostObject(device, "device");

            var nativeDevice = (ID3D12Device*)device;
            var queueDescription = default(D3D12_COMMAND_QUEUE_DESC);
            var queueId = QueueId;
            var createQueue =
                (delegate* unmanaged[Stdcall]<nint, D3D12_COMMAND_QUEUE_DESC*, Guid*, nint*, int>)Slot(device, 8);

            CheckHostResult(createQueue(device, &queueDescription, &queueId, &queue), "R42 direct queue");

            RequireHostObject(queue, "queue");

            var fenceId = FenceId;
            var createFence = (delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device, 36);

            CheckHostResult(createFence(device, 0, 0, &fenceId, &fence), "R42 fence");

            RequireHostObject(fence, "fence");

            var description = new ResourceDescription
            {
                Dimension = 3,
                Width = (ulong)width,
                Height = (uint)height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = 103, // DXGI_FORMAT_NV12
                SampleCount = 1
            };

            texture = CreateHostTransferResource(device, description, 1, CopyDestination);

            var layouts = stackalloc D3D12_PLACED_SUBRESOURCE_FOOTPRINT[2];
            var rows = stackalloc uint[2];
            var rowBytes = stackalloc ulong[2];
            var totalBytes = 0UL;

            nativeDevice->GetCopyableFootprints((D3D12_RESOURCE_DESC*)&description, 0, 2, 0,
                layouts, rows, rowBytes, &totalBytes);

            var lumaEnd = ValidateTextureFootprint(layouts[0], rows[0], rowBytes[0], width, height);
            var chromaEnd = ValidateTextureFootprint(layouts[1], rows[1], rowBytes[1], width, height / 2);
            var uploadBytes = Math.Max(totalBytes, Math.Max(lumaEnd, chromaEnd));

            if (layouts[0].Offset != 0 || layouts[1].Offset < lumaEnd || uploadBytes > 64UL * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid or overlapping NV12 copy footprints.");
            }

            upload = CreateHostTransferResource(device, HostBufferDescription(uploadBytes), 2, GenericRead);

            FillNv12Upload(upload, checked((int)uploadBytes), layouts, luma, lumaStride, chroma, chromaStride,
                width, height);

            var outputDescription = HostBufferDescription((ulong)outputBytes);

            outputDescription.Flags = 4; // ALLOW_UNORDERED_ACCESS

            output = CreateHostTransferResource(device, outputDescription, 1, 8); // UNORDERED_ACCESS
            readback = CreateHostTransferResource(device, HostBufferDescription((ulong)outputBytes), 3, CopyDestination);

            CreateNv12ConversionPipeline(device, report, out root, out pipeline);

            var heapDescription = new DescriptorHeapDescription { Kind = 0, Count = 2, Flags = 1 };
            var heapId = DescriptorHeapId;
            var createHeap =
                (delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(device, 14);

            CheckHostResult(createHeap(device, &heapDescription, &heapId, &heap), "R42 shader-visible SRV heap");

            RequireHostObject(heap, "descriptor heap");

            var nativeHeap = (ID3D12DescriptorHeap*)heap;
            var cpu = nativeHeap->GetCPUDescriptorHandleForHeapStart();
            var gpu = nativeHeap->GetGPUDescriptorHandleForHeapStart();
            var increment = nativeDevice->GetDescriptorHandleIncrementSize(0);

            if (cpu.ptr == 0 || gpu.ptr == 0 || increment == 0)
            {
                throw new InvalidOperationException("Invalid R42 descriptor handles.");
            }

            var yView = new VideoPlaneView { Format = 61, Dimension = 4, Mapping = 5768, MipLevels = 1 };
            var uvView = new VideoPlaneView { Format = 49, Dimension = 4, Mapping = 5768, MipLevels = 1, PlaneSlice = 1 };

            // R8_UNORM for plane zero and R8G8_UNORM for plane one of the same resource.
            nativeDevice->CreateShaderResourceView((ID3D12Resource*)texture, (D3D12_SHADER_RESOURCE_VIEW_DESC*)&yView,
                cpu);

            cpu.ptr += increment;

            nativeDevice->CreateShaderResourceView((ID3D12Resource*)texture, (D3D12_SHADER_RESOURCE_VIEW_DESC*)&uvView,
                cpu);

            var allocatorId = AllocatorId;
            var createAllocator = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(device, 9);

            CheckHostResult(createAllocator(device, 0, &allocatorId, &allocator), "R42 allocator");

            RequireHostObject(allocator, "allocator");

            var listId = HostListId;
            var createList =
                (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12);

            CheckHostResult(createList(device, 0, 0, allocator, 0, &listId, &list), "R42 command list");

            RequireHostObject(list, "command list");

            var commands = (ID3D12GraphicsCommandList*)list;

            for (var plane = 0; plane < 2; plane++)
            {
                var source = new TextureCopyLocation { Resource = upload, Type = 1, Footprint = layouts[plane] };
                var destination = new TextureCopyLocation { Resource = texture, Subresource = (uint)plane };

                commands->CopyTextureRegion((D3D12_TEXTURE_COPY_LOCATION*)&destination, 0, 0, 0,
                    (D3D12_TEXTURE_COPY_LOCATION*)&source, null);
            }

            TransitionHostResource(list, texture, CopyDestination, 0x40); // NON_PIXEL_SHADER_RESOURCE

            commands->SetDescriptorHeaps(1, &nativeHeap);

            commands->SetPipelineState((ID3D12PipelineState*)pipeline);

            commands->SetComputeRootSignature((ID3D12RootSignature*)root);

            commands->SetComputeRootDescriptorTable(0, gpu);

            var outputAddress = ((ID3D12Resource*)output)->GetGPUVirtualAddress();

            if (outputAddress == 0 || (outputAddress & 3UL) != 0)
            {
                throw new InvalidOperationException("Invalid R42 UAV address.");
            }

            commands->SetComputeRootUnorderedAccessView(1, outputAddress);

            commands->Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);

            // The transition orders prior UAV writes before the copy.
            TransitionHostResource(list, output, 8, CopySource);

            commands->CopyBufferRegion((ID3D12Resource*)readback, 0, (ID3D12Resource*)output, 0, (ulong)outputBytes);

            SubmitHostCommandsAndWait(queue, fence, list, completion);

            CopyHostReadback(readback, bgra[..outputBytes]);

            report.WriteLine($"[media] IRX-R42: NV12={width}x{height}; planes=2; " +
                             $"pitches={layouts[0].Footprint.RowPitch},{layouts[1].Footprint.RowPitch}; " +
                             $"offsets={layouts[0].Offset},{layouts[1].Offset}; uploadBytes={uploadBytes}; " +
                             $"readbackBytes={outputBytes}; fence=1.");
        }
        finally
        {
            // SubmitHostCommandsAndWait never returns while commands could still reference these objects.
            ReleaseHostObject(list);

            ReleaseHostObject(allocator);

            ReleaseHostObject(heap);

            ReleaseHostObject(pipeline);

            ReleaseHostObject(root);

            ReleaseHostObject(readback);

            ReleaseHostObject(output);

            ReleaseHostObject(upload);

            ReleaseHostObject(texture);

            ReleaseHostObject(fence);

            ReleaseHostObject(queue);

            ReleaseHostObject(device);
        }
    }

    private static void ValidateVideoPlane(int length, int stride, int width, int rows)
    {
        if (stride < width || (long)(rows - 1) * stride + width > length)
        {
            throw new ArgumentException("NV12 source plane exceeds its backing buffer.");
        }
    }

    internal static ulong ValidateTextureFootprint(D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout, uint rows,
        ulong rowBytes, int width, int expectedRows)
    {
        var pitch = layout.Footprint.RowPitch;

        if (rows != (uint)expectedRows || rowBytes != (ulong)width || pitch < (uint)width ||
            (pitch & 255U) != 0 || (layout.Offset & 511UL) != 0 || layout.Footprint.Depth != 1)
        {
            throw new InvalidDataException("D3D12 returned an incompatible NV12 plane footprint.");
        }

        return checked(layout.Offset + (ulong)pitch * rows);
    }

    internal static nint CreateHostTransferResource(nint device, ResourceDescription description, uint kind, uint state)
    {
        var properties = new GraphicsHeapProperties { Kind = kind, CreationNodes = 1, VisibleNodes = 1 };
        var iid = ResourceId;
        var resource = nint.Zero;
        var create = (delegate* unmanaged[Stdcall]<nint, GraphicsHeapProperties*, uint, ResourceDescription*, uint,
            void*, Guid*, nint*, int>)Slot(device, 27);

        var resultCode = create(device, &properties, 0, &description, state, null, &iid, &resource);

        if (resultCode < 0)
        {
            ReleaseHostObject(resource);

            CheckHostResult(resultCode, "R42 committed resource");
        }

        RequireHostObject(resource, "resource");

        return resource;
    }

    private static void FillNv12Upload(nint upload, int length, D3D12_PLACED_SUBRESOURCE_FOOTPRINT* layouts,
        ReadOnlySpan<byte> luma, int lumaStride, ReadOnlySpan<byte> chroma, int chromaStride, int width, int height)
    {
        var range = default(HostRange);
        var mapped = (void*)null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(upload, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(upload, 9);

        CheckHostResult(map(upload, 0, &range, &mapped), "R42 upload map");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("R42 upload map returned null.");
            }

            var bytes = new Span<byte>(mapped, length);

            bytes.Clear();

            CopyVideoRows(luma, lumaStride, bytes, layouts[0], width, height);

            CopyVideoRows(chroma, chromaStride, bytes, layouts[1], width, height / 2);
        }
        finally
        {
            var written = new HostRange { End = (nuint)length };

            unmap(upload, 0, &written);
        }
    }

    internal static void CopyVideoRows(ReadOnlySpan<byte> source, int stride, Span<byte> destination,
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT layout, int width, int rows)
    {
        var offset = checked((int)layout.Offset);
        var pitch = checked((int)layout.Footprint.RowPitch);

        PitchedPlaneCopy.Copy(source, stride, destination, offset, pitch, width, rows);
    }

    internal static void CreateNv12ConversionPipeline(nint device, TextWriter report, out nint root, out nint pipeline,
        string? shaderSource = null)
    {
        root = 0;

        pipeline = 0;

        var shader = CompileHostMediaShader(shaderSource ?? Nv12ConversionShader, report);
        var parameters = stackalloc D3D12_ROOT_PARAMETER1[2];
        var range = new D3D12_DESCRIPTOR_RANGE1 { NumDescriptors = 2 };

        parameters[0] = new D3D12_ROOT_PARAMETER1 { ParameterType = 0 };

        parameters[0].Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE1
        {
            NumDescriptorRanges = 1,
            pDescriptorRanges = &range
        };

        parameters[1] = new D3D12_ROOT_PARAMETER1 { ParameterType = (D3D12_ROOT_PARAMETER_TYPE)4 };

        parameters[1].Anonymous.Descriptor = new D3D12_ROOT_DESCRIPTOR1 { ShaderRegister = 0 };

        var description = new D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version = (D3D_ROOT_SIGNATURE_VERSION)2 };

        description.Anonymous.Desc_1_1 = new D3D12_ROOT_SIGNATURE_DESC1 { NumParameters = 2, pParameters = parameters };

        var blob = nint.Zero;
        var errors = nint.Zero;
        var nativeRoot = (void*)null;
        var nativePipeline = (void*)null;

        try
        {
            var resultCode = SerializeRootNative(&description, &blob, &errors);

            if (errors != 0)
            {
                report.WriteLine(Encoding.UTF8.GetString(ReadShaderBlob(errors, 1024 * 1024)).TrimEnd('\0'));
            }

            CheckHostResult(resultCode, "R42 root serialization");

            RequireHostObject(blob, "root blob");

            var rootBytes = ReadShaderBlob(blob, 1024 * 1024);
            var nativeDevice = (ID3D12Device*)device;
            var rootId = RootSignatureId;

            fixed (byte* bytes = rootBytes)
            {
                CheckHostResult(
                    nativeDevice->CreateRootSignature(0, bytes, (nuint)rootBytes.Length, &rootId, &nativeRoot),
                    "R42 root signature");
            }

            RequireHostObject((nint)nativeRoot, "root signature");

            var pipelineId = PipelineId;

            fixed (byte* bytes = shader)
            {
                var desc = new D3D12_COMPUTE_PIPELINE_STATE_DESC
                {
                    pRootSignature = (ID3D12RootSignature*)nativeRoot,
                    CS = new D3D12_SHADER_BYTECODE { pShaderBytecode = bytes, BytecodeLength = (nuint)shader.Length }
                };

                CheckHostResult(nativeDevice->CreateComputePipelineState(&desc, &pipelineId, &nativePipeline),
                    "R42 compute pipeline");
            }

            RequireHostObject((nint)nativePipeline, "pipeline");

            root = (nint)nativeRoot;

            pipeline = (nint)nativePipeline;

            nativeRoot = null;

            nativePipeline = null;
        }
        finally
        {
            ReleaseHostObject((nint)nativePipeline);

            ReleaseHostObject((nint)nativeRoot);

            ReleaseHostObject(errors);

            ReleaseHostObject(blob);
        }
    }

    private static void SubmitHostCommandsAndWait(nint queue, nint fence, nint list, EventWaitHandle completion)
    {
        var close = (delegate* unmanaged[Stdcall]<nint, int>)Slot(list, 9);
        var execute = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10);
        var signal = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(queue, 14);
        var getCompleted = (delegate* unmanaged[Stdcall]<nint, ulong>)Slot(fence, 8);
        var setEvent = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(fence, 9);
        var eventHandle = completion.SafeWaitHandle.DangerousGetHandle();

        CheckHostResult(close(list), "R42 close command list");

        // Once submitted, releasing potentially in-flight resources would be unsafe.
        // Fail fast on any loss of completion proof, as the existing host fence code does.
        execute(queue, 1, &list);

        try
        {
            var resultCode = signal(queue, fence, 1);

            if (resultCode < 0)
            {
                Environment.FailFast($"R42 queue signal failed: 0x{resultCode:X8}.");
            }

            var value = getCompleted(fence);

            if (value < 1)
            {
                resultCode = setEvent(fence, 1, eventHandle);

                if (resultCode < 0 || !completion.WaitOne(TimeSpan.FromSeconds(15)))
                {
                    Environment.FailFast($"R42 GPU completion wait failed: 0x{resultCode:X8}.");
                }

                value = getCompleted(fence);
            }

            if (value != 1)
            {
                Environment.FailFast($"R42 invalid fence completion: {value}.");
            }
        }
        catch (Exception exception)
        {
            Environment.FailFast("R42 could not prove GPU completion.", exception);
        }
    }

    private static void CopyHostReadback(nint readback, Span<byte> destination)
    {
        var range = new HostRange { End = (nuint)destination.Length };
        var mapped = (void*)null;
        var map = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void**, int>)Slot(readback, 8);
        var unmap = (delegate* unmanaged[Stdcall]<nint, uint, HostRange*, void>)Slot(readback, 9);

        CheckHostResult(map(readback, 0, &range, &mapped), "R42 readback map");

        try
        {
            if (mapped == null)
            {
                throw new InvalidOperationException("R42 readback map returned null.");
            }

            new ReadOnlySpan<byte>(mapped, destination.Length).CopyTo(destination);
        }
        finally
        {
            var noWrites = default(HostRange);

            unmap(readback, 0, &noWrites);
        }
    }

    internal static void RequireHostObject(nint value, string name)
    {
        if (value == 0)
        {
            throw new InvalidOperationException($"R42 returned a null {name}.");
        }
    }

    internal static void ReleaseHostObject(nint value)
    {
        if (value != 0)
        {
            _ = HostRelease(value);
        }
    }
}
