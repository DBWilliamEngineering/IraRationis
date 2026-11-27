/*
* @file        D3D12MemoryProbeSession.cs
* @description Owns the D3D12 resources and GPU timing for one synthetic memory benchmark mode.
* @author      Damon William
* @date        2026-11-27
*/
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using IraRationis.Cli.Diagnostics.Memory.Models;
using IraRationis.Memory.VirtualMemory;
using IraRationis.Memory.VirtualMemory.Models;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D12_COMMAND_LIST_TYPE;
using static TerraFX.Interop.DirectX.D3D12_HEAP_TYPE;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_FLAGS;
using static TerraFX.Interop.DirectX.D3D12_RESOURCE_STATES;
using Win32 = global::TerraFX.Interop.Windows.Windows;

namespace IraRationis.Cli.Diagnostics.Memory;

internal sealed unsafe class D3D12MemoryProbeSession(MemoryResidency mode, ulong bytes) : IDisposable
{
    internal const int Unsupported = unchecked((int)0x887A0004);
    private const int OutOfMemory = unchecked((int)0x8007000E);
    private const int LaneCount = 256;
    private const int WordsPerLane = 16;
    private const int GroupWords = LaneCount * WordsPerLane;
    private const int GroupBytes = GroupWords * sizeof(uint);
    private const uint BeforeCopyTimestamp = 0;
    private const uint BeforeComputeTimestamp = 1;
    private const uint AfterComputeTimestamp = 2;
    private const uint TimestampCount = 3;
    private const uint TimestampBytes = TimestampCount * sizeof(ulong);
    private const uint InputRootIndex = 0;
    private const uint OutputRootIndex = 1;
    private const uint WaitSignaled = 0;
    private const uint FenceTimeoutMilliseconds = 30_000;
    private const uint FenceFailureExitCode = 3;
    private const uint ShaderOptimizationLevel3 = 1U << 15;

    private static readonly byte[] Shader = Encoding.UTF8.GetBytes(FormattableString.Invariant($$"""
        #define LANES {{LaneCount}}
        #define WORDS_PER_LANE {{WordsPerLane}}
        #define WORD_BYTES {{sizeof(uint)}}
        ByteAddressBuffer inputData : register(t0);
        RWByteAddressBuffer outputData : register(u0);
        groupshared uint sums[LANES];

        [numthreads(LANES, 1, 1)]
        void main(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
        {
            uint total = 0;
            [unroll]
            for (uint word = 0; word < WORDS_PER_LANE; ++word)
            {
                uint index = group.x * LANES * WORDS_PER_LANE + word * LANES + lane;
                total += inputData.Load(index * WORD_BYTES);
            }

            sums[lane] = total;
            GroupMemoryBarrierWithGroupSync();
            for (uint stride = LANES / 2; stride > 0; stride >>= 1)
            {
                if (lane < stride) sums[lane] += sums[lane + stride];
                GroupMemoryBarrierWithGroupSync();
            }

            if (lane == 0) outputData.Store(group.x * WORD_BYTES, sums[0]);
        }
        """));

    private ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12CommandQueue> _queue;
    private ComPtr<ID3D12CommandAllocator> _allocator;
    private ComPtr<ID3D12GraphicsCommandList> _commands;
    private ComPtr<ID3D12Fence> _fence;
    private ComPtr<ID3D12RootSignature> _root;
    private ComPtr<ID3D12PipelineState> _pipeline;
    private ComPtr<ID3D12QueryHeap> _queries;
    private ComPtr<ID3D12Heap> _existingHeap;
    private ComPtr<ID3D12Resource> _source;
    private ComPtr<ID3D12Resource> _staging;
    private ComPtr<ID3D12Resource> _output;
    private ComPtr<ID3D12Resource> _readback;
    private ComPtr<ID3D12Resource> _timestamps;
    private HANDLE _completion;
    private void* _ram;
    private void* _cpuAddress;
    private ulong _fenceValue;
    private ulong _timestampFrequency;

    internal nuint CpuAddress
    {
        get
        {
            return (nuint)_cpuAddress;
        }
    }

    internal ulong GpuAddress
    {
        get
        {
            return _source.Get()->GetGPUVirtualAddress();
        }
    }

    internal static void ReadAdapter(uint index, out string name, out ulong dedicatedBytes)
    {
        ComPtr<IDXGIAdapter1> adapter = default;

        try
        {
            OpenAdapter(index, ref adapter, out var description);

            ReadOnlySpan<char> characters = description.Description;
            var end = characters.IndexOf('\0');
            name = new string(end < 0 ? characters : characters[..end]);
            dedicatedBytes = description.DedicatedVideoMemory;
        }
        finally
        {
            adapter.Dispose();
        }
    }

    internal void Initialize(uint adapterIndex)
    {
        ComPtr<IDXGIAdapter1> adapter = default;

        try
        {
            OpenAdapter(adapterIndex, ref adapter, out _);

            fixed (ID3D12Device** device = _device)
            {
                Check(DirectX.D3D12CreateDevice((IUnknown*)adapter.Get(), D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
                    Win32.__uuidof<ID3D12Device>(), (void**)device));
            }
        }
        finally
        {
            adapter.Dispose();
        }

        CreateSource();

        var checksumBytes = bytes / GroupBytes * sizeof(uint);
        Allocate(ref _output, D3D12_HEAP_TYPE_DEFAULT, checksumBytes, D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
        Allocate(ref _readback, D3D12_HEAP_TYPE_READBACK, checksumBytes, D3D12_RESOURCE_STATE_COPY_DEST);
        Allocate(ref _timestamps, D3D12_HEAP_TYPE_READBACK, TimestampBytes, D3D12_RESOURCE_STATE_COPY_DEST);

        CreatePipeline();
        CreateSubmissionObjects();
    }

    internal MemoryProbeSample Run(uint seed)
    {
        var stagingMs = mode == MemoryResidency.MirroredLocal ? StageMirroredSource() : 0;

        Sse.StoreFence();
        Check(_allocator.Get()->Reset());

        var commands = _commands.Get();
        var queries = _queries.Get();
        const D3D12_QUERY_TYPE timestamp = D3D12_QUERY_TYPE.D3D12_QUERY_TYPE_TIMESTAMP;
        Check(commands->Reset(_allocator.Get(), _pipeline.Get()));
        commands->EndQuery(queries, timestamp, BeforeCopyTimestamp);

        if (mode == MemoryResidency.MirroredLocal)
        {
            var source = _source.Get();
            Transition(commands, source, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                D3D12_RESOURCE_STATE_COPY_DEST);
            commands->CopyBufferRegion(source, 0, _staging.Get(), 0, bytes);
            Transition(commands, source, D3D12_RESOURCE_STATE_COPY_DEST,
                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        }

        var output = _output.Get();
        var groups = (uint)(bytes / GroupBytes);
        var checksumBytes = (ulong)groups * sizeof(uint);
        commands->EndQuery(queries, timestamp, BeforeComputeTimestamp);
        commands->SetComputeRootSignature(_root.Get());
        commands->SetComputeRootShaderResourceView(InputRootIndex, GpuAddress);
        commands->SetComputeRootUnorderedAccessView(OutputRootIndex, output->GetGPUVirtualAddress());
        commands->Dispatch(groups, 1, 1);
        commands->EndQuery(queries, timestamp, AfterComputeTimestamp);
        Transition(commands, output, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
        commands->CopyBufferRegion(_readback.Get(), 0, output, 0, checksumBytes);
        Transition(commands, output, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        commands->ResolveQueryData(queries, timestamp, BeforeCopyTimestamp, TimestampCount, _timestamps.Get(), 0);
        Check(commands->Close());

        var submitMs = SubmitAndWait();
        Check(_device.Get()->GetDeviceRemovedReason());
        ReadTimings(out var copyMs, out var computeMs);
        var mismatches = CountMismatches(seed, groups, checksumBytes);

        return new MemoryProbeSample(stagingMs, copyMs, computeMs, submitMs, mismatches);
    }

    public void Dispose()
    {
        if (_completion != default)
        {
            _ = Win32.CloseHandle(_completion);
            _completion = default;
        }

        if (_cpuAddress != null && _ram == null)
        {
            _source.Get()->Unmap(0, null);
        }

        _cpuAddress = null;
        _commands.Dispose();
        _allocator.Dispose();
        _fence.Dispose();
        _queue.Dispose();
        _queries.Dispose();
        _pipeline.Dispose();
        _root.Dispose();
        _timestamps.Dispose();
        _readback.Dispose();
        _output.Dispose();
        _staging.Dispose();
        _source.Dispose();
        _existingHeap.Dispose();

        if (_ram != null)
        {
            _ = Win32.VirtualFree(_ram, 0, MEM.MEM_RELEASE);
            _ram = null;
        }

        _device.Dispose();
    }

    private static void OpenAdapter(uint index, ref ComPtr<IDXGIAdapter1> adapter, out DXGI_ADAPTER_DESC1 description)
    {
        ComPtr<IDXGIFactory6> factory = default;

        try
        {
            Check(DirectX.CreateDXGIFactory2(0, Win32.__uuidof<IDXGIFactory6>(), (void**)factory.GetAddressOf()));

            fixed (IDXGIAdapter1** result = adapter)
            {
                Check(factory.Get()->EnumAdapterByGpuPreference(index,
                    DXGI_GPU_PREFERENCE.DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, Win32.__uuidof<IDXGIAdapter1>(),
                    (void**)result));
            }

            DXGI_ADAPTER_DESC1 info;
            Check(adapter.Get()->GetDesc1(&info));

            if ((info.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
            {
                throw new COMException("A hardware adapter is required.", Unsupported);
            }

            description = info;
        }
        finally
        {
            factory.Dispose();
        }
    }

    private void CreateSource()
    {
        if (mode == MemoryResidency.GpuUpload)
        {
            D3D12_FEATURE_DATA_D3D12_OPTIONS16 options = default;
            var result = _device.Get()->CheckFeatureSupport(D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS16, &options,
                (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS16));

            if (result.Value < 0 || !options.GPUUploadHeapSupported)
            {
                throw new COMException("The device does not support GPU upload heaps.", Unsupported);
            }
        }

        if (mode is MemoryResidency.MirroredLocal or MemoryResidency.ExistingSystemHeap)
        {
            _ram = Win32.VirtualAlloc(null, (nuint)bytes, MEM.MEM_RESERVE | MEM.MEM_COMMIT, PAGE.PAGE_READWRITE);
            _cpuAddress = _ram;

            if (_ram == null)
            {
                throw new COMException("Unable to allocate benchmark system memory.", OutOfMemory);
            }
        }

        switch (mode)
        {
            case MemoryResidency.ExistingSystemHeap:
                CreateExistingHeapSource();
                break;

            case MemoryResidency.MirroredLocal:
                Allocate(ref _staging, D3D12_HEAP_TYPE_UPLOAD, bytes, D3D12_RESOURCE_STATE_GENERIC_READ);
                Allocate(ref _source, D3D12_HEAP_TYPE_DEFAULT, bytes, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
                break;

            default:
                var heap = mode == MemoryResidency.GpuUpload ? D3D12_HEAP_TYPE_GPU_UPLOAD : D3D12_HEAP_TYPE_UPLOAD;
                Allocate(ref _source, heap, bytes, D3D12_RESOURCE_STATE_GENERIC_READ);

                D3D12_RANGE noRead = default;
                void* mapped = null;
                Check(_source.Get()->Map(0, &noRead, &mapped));
                _cpuAddress = mapped;
                break;
        }
    }

    private void CreateExistingHeapSource()
    {
        ComPtr<ID3D12Device3> device = default;

        try
        {
            Check(_device.Get()->QueryInterface(Win32.__uuidof<ID3D12Device3>(), (void**)device.GetAddressOf()));

            fixed (ID3D12Heap** existingHeap = _existingHeap)
            {
                Check(device.Get()->OpenExistingHeapFromAddress(_ram, Win32.__uuidof<ID3D12Heap>(),
                    (void**)existingHeap));
            }

            var description = DescribeBuffer(bytes, D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER);

            fixed (ID3D12Resource** source = _source)
            {
                Check(_device.Get()->CreatePlacedResource(_existingHeap.Get(), 0, &description,
                    D3D12_RESOURCE_STATE_COMMON, null, Win32.__uuidof<ID3D12Resource>(), (void**)source));
            }
        }
        finally
        {
            device.Dispose();
        }
    }

    private void Allocate(ref ComPtr<ID3D12Resource> resource, D3D12_HEAP_TYPE heapType, ulong size,
        D3D12_RESOURCE_STATES state, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
    {
        var properties = new D3D12_HEAP_PROPERTIES { Type = heapType, CreationNodeMask = 1, VisibleNodeMask = 1 };
        var description = DescribeBuffer(size, flags);

        fixed (ID3D12Resource** output = resource)
        {
            Check(_device.Get()->CreateCommittedResource(&properties, D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
                &description, state, null, Win32.__uuidof<ID3D12Resource>(), (void**)output));
        }
    }

    private static D3D12_RESOURCE_DESC DescribeBuffer(ulong size, D3D12_RESOURCE_FLAGS flags)
    {
        return new D3D12_RESOURCE_DESC
        {
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_BUFFER,
            Width = size,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            Flags = flags
        };
    }

    private void CreatePipeline()
    {
        const uint rootParameterCount = 2;
        var parameters = stackalloc D3D12_ROOT_PARAMETER[(int)rootParameterCount];
        parameters[InputRootIndex] = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_SRV
        };
        parameters[OutputRootIndex] = new D3D12_ROOT_PARAMETER
        {
            ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_UAV
        };
        var rootDescription = new D3D12_ROOT_SIGNATURE_DESC
        {
            NumParameters = rootParameterCount,
            pParameters = parameters
        };
        ComPtr<ID3DBlob> serializedRoot = default;
        ComPtr<ID3DBlob> bytecode = default;

        try
        {
            Check(DirectX.D3D12SerializeRootSignature(&rootDescription,
                D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1, serializedRoot.GetAddressOf(), null));

            var blob = serializedRoot.Get();
            fixed (ID3D12RootSignature** root = _root)
            {
                Check(_device.Get()->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(),
                    Win32.__uuidof<ID3D12RootSignature>(), (void**)root));
            }

            fixed (byte* source = Shader, entry = "main"u8, profile = "cs_5_1"u8)
            {
                Check(DirectX.D3DCompile(source, (nuint)Shader.Length, null, null, null,
                    (sbyte*)entry, (sbyte*)profile, ShaderOptimizationLevel3, 0,
                    bytecode.GetAddressOf(), null));
            }

            blob = bytecode.Get();
            var pipelineDescription = new D3D12_COMPUTE_PIPELINE_STATE_DESC
            {
                pRootSignature = _root.Get(),
                CS = new D3D12_SHADER_BYTECODE
                {
                    pShaderBytecode = blob->GetBufferPointer(),
                    BytecodeLength = blob->GetBufferSize()
                }
            };

            fixed (ID3D12PipelineState** pipeline = _pipeline)
            {
                Check(_device.Get()->CreateComputePipelineState(&pipelineDescription,
                    Win32.__uuidof<ID3D12PipelineState>(), (void**)pipeline));
            }
        }
        finally
        {
            bytecode.Dispose();
            serializedRoot.Dispose();
        }
    }

    private void CreateSubmissionObjects()
    {
        const D3D12_COMMAND_LIST_TYPE direct = D3D12_COMMAND_LIST_TYPE_DIRECT;
        var queueDescription = new D3D12_COMMAND_QUEUE_DESC { Type = direct };

        fixed (ID3D12CommandQueue** queue = _queue)
        {
            Check(_device.Get()->CreateCommandQueue(&queueDescription, Win32.__uuidof<ID3D12CommandQueue>(),
                (void**)queue));
        }

        ulong frequency = 0;
        Check(_queue.Get()->GetTimestampFrequency(&frequency));
        _timestampFrequency = frequency;

        fixed (ID3D12CommandAllocator** allocator = _allocator)
        {
            Check(_device.Get()->CreateCommandAllocator(direct, Win32.__uuidof<ID3D12CommandAllocator>(),
                (void**)allocator));
        }

        fixed (ID3D12GraphicsCommandList** commands = _commands)
        {
            Check(_device.Get()->CreateCommandList(0, direct, _allocator.Get(), _pipeline.Get(),
                Win32.__uuidof<ID3D12GraphicsCommandList>(), (void**)commands));
        }

        Check(_commands.Get()->Close());

        var queryDescription = new D3D12_QUERY_HEAP_DESC
        {
            Type = D3D12_QUERY_HEAP_TYPE.D3D12_QUERY_HEAP_TYPE_TIMESTAMP,
            Count = TimestampCount
        };

        fixed (ID3D12QueryHeap** queries = _queries)
        {
            Check(_device.Get()->CreateQueryHeap(&queryDescription, Win32.__uuidof<ID3D12QueryHeap>(), (void**)queries));
        }

        fixed (ID3D12Fence** fence = _fence)
        {
            Check(_device.Get()->CreateFence(0, D3D12_FENCE_FLAGS.D3D12_FENCE_FLAG_NONE,
                Win32.__uuidof<ID3D12Fence>(), (void**)fence));
        }

        _completion = CreateEventW(null, false, false, null);

        if (_completion == default)
        {
            Check(Marshal.GetHRForLastWin32Error());
        }
    }

    private double StageMirroredSource()
    {
        D3D12_RANGE noRead = default;
        void* mapped = null;
        var staging = _staging.Get();
        Check(staging->Map(0, &noRead, &mapped));

        var started = Stopwatch.GetTimestamp();
        Buffer.MemoryCopy(_ram, mapped, bytes, bytes);
        Sse.StoreFence();
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        staging->Unmap(0, null);

        return elapsedMs;
    }

    private double SubmitAndWait()
    {
        var list = (ID3D12CommandList*)_commands.Get();
        var queue = _queue.Get();
        var fence = _fence.Get();
        var started = Stopwatch.GetTimestamp();
        queue->ExecuteCommandLists(1, &list);
        var value = ++_fenceValue;

        // A failed submission wait leaves resource ownership with the GPU.
        if (queue->Signal(fence, value).Value < 0 || fence->SetEventOnCompletion(value, _completion).Value < 0 ||
            Win32.WaitForSingleObject(_completion, FenceTimeoutMilliseconds) != WaitSignaled)
        {
            _ = Win32.TerminateProcess(Win32.GetCurrentProcess(), FenceFailureExitCode);
            Environment.FailFast("D3D12 memory probe could not establish GPU completion.");
        }

        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void ReadTimings(out double copyMs, out double computeMs)
    {
        var resource = _timestamps.Get();
        var range = new D3D12_RANGE { End = TimestampBytes };
        D3D12_RANGE noWrite = default;
        void* mapped = null;
        Check(resource->Map(0, &range, &mapped));

        var timestamps = (ulong*)mapped;
        var scale = 1000.0 / _timestampFrequency;
        copyMs = (timestamps[BeforeComputeTimestamp] - timestamps[BeforeCopyTimestamp]) * scale;
        computeMs = (timestamps[AfterComputeTimestamp] - timestamps[BeforeComputeTimestamp]) * scale;
        resource->Unmap(0, &noWrite);
    }

    private uint CountMismatches(uint seed, uint groups, ulong checksumBytes)
    {
        const uint groupsPerPage = (uint)(VirtualPageTable.PageSize / GroupBytes);
        var resource = _readback.Get();
        var range = new D3D12_RANGE { End = (nuint)checksumBytes };
        D3D12_RANGE noWrite = default;
        void* mapped = null;
        Check(resource->Map(0, &range, &mapped));

        var sums = (uint*)mapped;
        var mismatches = 0U;

        for (var group = 0U; group < groups; group++)
        {
            if (sums[group] != GroupWords * (seed + group / groupsPerPage))
            {
                mismatches++;
            }
        }

        resource->Unmap(0, &noWrite);

        return mismatches;
    }

    private static void Transition(ID3D12GraphicsCommandList* commands, ID3D12Resource* resource,
        D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        var barrier = new D3D12_RESOURCE_BARRIER
        {
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
            Transition = new D3D12_RESOURCE_TRANSITION_BARRIER
            {
                pResource = resource,
                Subresource = uint.MaxValue,
                StateBefore = before,
                StateAfter = after
            }
        };
        commands->ResourceBarrier(1, &barrier);
    }

    private static void Check(HRESULT result)
    {
        if (result.Value < 0)
        {
            throw new COMException("The D3D12 memory probe operation failed.", result.Value);
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HANDLE CreateEventW(void* attributes, BOOL manualReset, BOOL initialState, char* name);
}
