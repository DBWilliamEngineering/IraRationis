// SPDX-License-Identifier: GPL-3.0-or-later
// Synthetic D3D12 experiment. No title code or assets are used.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <cstdint>
#include <cstring>
#include <new>
#include <intrin.h>
using Microsoft::WRL::ComPtr;

namespace
{
    constexpr UINT GroupBytes = 16384;

    struct Probe
    {
        ComPtr<ID3D12Device> device;
        ComPtr<ID3D12CommandQueue> queue;
        ComPtr<ID3D12CommandAllocator> allocator;
        ComPtr<ID3D12GraphicsCommandList> list;
        ComPtr<ID3D12Fence> fence;
        ComPtr<ID3D12RootSignature> root;
        ComPtr<ID3D12PipelineState> pipeline;
        ComPtr<ID3D12QueryHeap> queries;
        ComPtr<ID3D12Heap> existing;
        ComPtr<ID3D12Resource> source, staging, output, readback, timestamps;
        HANDLE event = nullptr;
        void* mapped = nullptr;
        void* ram = nullptr;
        UINT64 bytes = 0, fenceValue = 0, frequency = 0;
        UINT mode = 0;

        ~Probe()
        {
            // Every successful submission is fenced before return. If a GPU hangs,
            // the run terminates rather than freeing resources still in flight.
            source.Reset();
            existing.Reset();
            if (ram)
            {
                VirtualFree(ram, 0, MEM_RELEASE);
            }
            if (event)
            {
                CloseHandle(event);
            }
        }
    };

    struct Result
    {
        double stagingMs, copyMs, computeMs, submitMs;
        UINT mismatches, reserved;
    };

    static_assert(sizeof(Result) == 40);
    constexpr char Shader[] = R"(
ByteAddressBuffer inputData : register(t0);
RWByteAddressBuffer outputData : register(u0);
groupshared uint sums[256];
[numthreads(256, 1, 1)]
void main(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint total = 0;
    [unroll] for (uint i = 0; i < 16; ++i)
        total += inputData.Load(group.x * 16384 + (i * 256 + lane) * 4);
    sums[lane] = total;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 128; stride > 0; stride >>= 1)
    {
        if (lane < stride) sums[lane] += sums[lane + stride];
        GroupMemoryBarrierWithGroupSync();
    }
    if (lane == 0) outputData.Store(group.x * 4, sums[0]);
})";

    D3D12_RESOURCE_DESC Buffer(UINT64 size, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
    {
        D3D12_RESOURCE_DESC d{};
        d.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        d.Width = size;
        d.Height = 1;
        d.DepthOrArraySize = 1;
        d.MipLevels = 1;
        d.SampleDesc.Count = 1;
        d.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        d.Flags = flags;
        return d;
    }

    HRESULT Allocate(Probe& p, D3D12_HEAP_TYPE heap, UINT64 size, D3D12_RESOURCE_STATES state,
                     ComPtr<ID3D12Resource>& out, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE)
    {
        D3D12_HEAP_PROPERTIES props{};
        props.Type = heap;
        props.CreationNodeMask = props.VisibleNodeMask = 1;
        auto desc = Buffer(size, flags);
        return p.device->CreateCommittedResource(&props, D3D12_HEAP_FLAG_NONE, &desc, state, nullptr,
                                                 IID_PPV_ARGS(&out));
    }

    void Transition(Probe& p, ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after)
    {
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition = {resource, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, before, after};
        p.list->ResourceBarrier(1, &b);
    }

    double Clock()
    {
        LARGE_INTEGER ticks{}, freq{};
        QueryPerformanceCounter(&ticks);
        QueryPerformanceFrequency(&freq);
        return static_cast<double>(ticks.QuadPart) * 1000.0 / static_cast<double>(freq.QuadPart);
    }

#define CHECK(call) do { const HRESULT hr = (call); if (FAILED(hr)) { return hr; } } while (false)

    HRESULT Init(Probe& p, UINT adapterIndex, UINT mode, UINT64 bytes)
    {
        p.mode = mode;
        p.bytes = bytes;
        ComPtr<IDXGIFactory6> factory;
        CHECK(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
        ComPtr<IDXGIAdapter1> adapter;
        CHECK(factory->EnumAdapterByGpuPreference(adapterIndex, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&
            adapter)));
        DXGI_ADAPTER_DESC1 info{};
        CHECK(adapter->GetDesc1(&info));
        if ((info.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
        {
            return DXGI_ERROR_UNSUPPORTED;
        }
        CHECK(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&p.device)));
        if (mode == 2)
        {
            D3D12_FEATURE_DATA_D3D12_OPTIONS16 opts{};
            if (FAILED(p.device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16, &opts,
                                                     static_cast<UINT>(sizeof(opts)))) || !opts.GPUUploadHeapSupported)
            {
                return DXGI_ERROR_UNSUPPORTED;
            }
        }
        if (mode == 3 || mode == 1)
        {
            p.ram = VirtualAlloc(nullptr, static_cast<SIZE_T>(bytes), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            if (!p.ram)
            {
                return E_OUTOFMEMORY;
            }
            p.mapped = p.ram;
        }
        if (mode == 3)
        {
            ComPtr<ID3D12Device3> device3;
            CHECK(p.device.As(&device3));
            CHECK(device3->OpenExistingHeapFromAddress(p.ram, IID_PPV_ARGS(&p.existing)));
            auto desc = Buffer(bytes, D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER);
            CHECK(p.device->CreatePlacedResource(p.existing.Get(), 0, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr,
                IID_PPV_ARGS(&p.source)));
        }
        else if (mode == 1)
        {
            CHECK(Allocate(p, D3D12_HEAP_TYPE_UPLOAD, bytes, D3D12_RESOURCE_STATE_GENERIC_READ, p.staging));
            CHECK(Allocate(p, D3D12_HEAP_TYPE_DEFAULT, bytes, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, p.source
            ));
        }
        else
        {
            CHECK(Allocate(p, mode == 2 ? D3D12_HEAP_TYPE_GPU_UPLOAD : D3D12_HEAP_TYPE_UPLOAD, bytes,
                D3D12_RESOURCE_STATE_GENERIC_READ, p.source));
            D3D12_RANGE noRead{0, 0};
            CHECK(p.source->Map(0, &noRead, &p.mapped));
        }
        const auto resultBytes = bytes / GroupBytes * 4;
        CHECK(Allocate(p, D3D12_HEAP_TYPE_DEFAULT, resultBytes, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, p.output,
            D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS));
        CHECK(Allocate(p, D3D12_HEAP_TYPE_READBACK, resultBytes, D3D12_RESOURCE_STATE_COPY_DEST, p.readback));
        CHECK(Allocate(p, D3D12_HEAP_TYPE_READBACK, 24, D3D12_RESOURCE_STATE_COPY_DEST, p.timestamps));
        D3D12_ROOT_PARAMETER params[2]{};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV;
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_UAV;
        D3D12_ROOT_SIGNATURE_DESC rd{};
        rd.NumParameters = 2;
        rd.pParameters = params;
        ComPtr<ID3DBlob> blob, errors, cs;
        CHECK(D3D12SerializeRootSignature(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &errors));
        CHECK(p.device->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&p.root)));
        CHECK(D3DCompile(Shader, sizeof(Shader) - 1, nullptr, nullptr, nullptr, "main", "cs_5_1",
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &cs, &errors));
        D3D12_COMPUTE_PIPELINE_STATE_DESC pd{};
        pd.pRootSignature = p.root.Get();
        pd.CS = {cs->GetBufferPointer(), cs->GetBufferSize()};
        CHECK(p.device->CreateComputePipelineState(&pd, IID_PPV_ARGS(&p.pipeline)));
        D3D12_COMMAND_QUEUE_DESC qd{};
        qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        CHECK(p.device->CreateCommandQueue(&qd, IID_PPV_ARGS(&p.queue)));
        CHECK(p.queue->GetTimestampFrequency(&p.frequency));
        CHECK(p.device->CreateCommandAllocator(qd.Type, IID_PPV_ARGS(&p.allocator)));
        CHECK(p.device->CreateCommandList(0, qd.Type, p.allocator.Get(), p.pipeline.Get(), IID_PPV_ARGS(&p.list)));
        CHECK(p.list->Close());
        D3D12_QUERY_HEAP_DESC query{};
        query.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP;
        query.Count = 3;
        CHECK(p.device->CreateQueryHeap(&query, IID_PPV_ARGS(&p.queries)));
        CHECK(p.device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&p.fence)));
        p.event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        return p.event ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    HRESULT Run(Probe& p, UINT seed, Result& r)
    {
        if (p.mode == 1)
        {
            void* staging = nullptr;
            D3D12_RANGE noRead{0, 0};
            CHECK(p.staging->Map(0, &noRead, &staging));
            const auto begin = Clock();
            std::memcpy(staging, p.ram, static_cast<size_t>(p.bytes));
            _mm_sfence();
            r.stagingMs = Clock() - begin;
            p.staging->Unmap(0, nullptr);
        }
        _mm_sfence();
        CHECK(p.allocator->Reset());
        CHECK(p.list->Reset(p.allocator.Get(), p.pipeline.Get()));
        p.list->EndQuery(p.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0);
        if (p.mode == 1)
        {
            Transition(p, p.source.Get(), D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,
                       D3D12_RESOURCE_STATE_COPY_DEST);
            p.list->CopyBufferRegion(p.source.Get(), 0, p.staging.Get(), 0, p.bytes);
            Transition(p, p.source.Get(), D3D12_RESOURCE_STATE_COPY_DEST,
                       D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
        }
        p.list->EndQuery(p.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 1);
        p.list->SetComputeRootSignature(p.root.Get());
        p.list->SetComputeRootShaderResourceView(0, p.source->GetGPUVirtualAddress());
        p.list->SetComputeRootUnorderedAccessView(1, p.output->GetGPUVirtualAddress());
        const auto groups = static_cast<UINT>(p.bytes / GroupBytes);
        p.list->Dispatch(groups, 1, 1);
        p.list->EndQuery(p.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 2);
        Transition(p, p.output.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE);
        p.list->CopyBufferRegion(p.readback.Get(), 0, p.output.Get(), 0, static_cast<UINT64>(groups) * 4);
        Transition(p, p.output.Get(), D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
        p.list->ResolveQueryData(p.queries.Get(), D3D12_QUERY_TYPE_TIMESTAMP, 0, 3, p.timestamps.Get(), 0);
        CHECK(p.list->Close());
        ID3D12CommandList* lists[] = {p.list.Get()};
        const auto start = Clock();
        p.queue->ExecuteCommandLists(1, lists);
        const auto value = ++p.fenceValue;
        // After submission, failure to establish completion cannot safely release resources.
        if (FAILED(p.queue->Signal(p.fence.Get(), value)) || FAILED(p.fence->SetEventOnCompletion(value, p.event)) ||
            WaitForSingleObject(p.event, 30000) != WAIT_OBJECT_0)
        {
            TerminateProcess(GetCurrentProcess(), 3);
            return E_FAIL;
        }
        r.submitMs = Clock() - start;
        CHECK(p.device->GetDeviceRemovedReason());
        void* times = nullptr;
        D3D12_RANGE timeRange{0, 24}, noWrite{0, 0};
        CHECK(p.timestamps->Map(0, &timeRange, &times));
        const auto t = static_cast<const UINT64*>(times);
        const auto scale = 1000.0 / static_cast<double>(p.frequency);
        r.copyMs = static_cast<double>(t[1] - t[0]) * scale;
        r.computeMs = static_cast<double>(t[2] - t[1]) * scale;
        p.timestamps->Unmap(0, &noWrite);
        void* data = nullptr;
        D3D12_RANGE readRange{0, static_cast<SIZE_T>(groups) * 4};
        CHECK(p.readback->Map(0, &readRange, &data));
        const auto sums = static_cast<const UINT*>(data);
        for (UINT group = 0; group < groups; ++group)
        {
            if (sums[group] != 4096u * (seed + group / 4u))
            {
                ++r.mismatches;
            }
        }
        p.readback->Unmap(0, &noWrite);
        return S_OK;
    }
}

extern "C" __declspec(dllexport) HRESULT irx_memory_create(UINT adapter, UINT mode, UINT64 bytes, void** session,
                                                           void** cpu, UINT64* gpu)
{
    if (!session || !cpu || !gpu || mode > 3 || bytes < 65536 || bytes > 256ull * 1024 * 1024 || bytes % 65536 != 0)
    {
        return E_INVALIDARG;
    }
    *session = nullptr;
    *cpu = nullptr;
    *gpu = 0;
    auto p = new(std::nothrow) Probe();
    if (!p)
    {
        return E_OUTOFMEMORY;
    }
    const auto hr = Init(*p, adapter, mode, bytes);
    if (FAILED(hr))
    {
        delete p;
        return hr;
    }
    *session = p;
    *cpu = p->mapped;
    *gpu = p->source->GetGPUVirtualAddress();
    return S_OK;
}

extern "C" __declspec(dllexport) HRESULT irx_memory_run(void* session, UINT seed, Result* result)
{
    if (!session || !result)
    {
        return E_INVALIDARG;
    }
    *result = {};
    return Run(*static_cast<Probe*>(session), seed, *result);
}

extern "C" __declspec(dllexport) void irx_memory_destroy(void* session)
{
    delete static_cast<Probe*>(session);
}

extern "C" __declspec(dllexport) HRESULT irx_memory_adapter(UINT index, wchar_t* description, UINT capacity,
                                                            UINT64* dedicatedBytes)
{
    if (!description || capacity < 128 || !dedicatedBytes)
    {
        return E_INVALIDARG;
    }
    ComPtr<IDXGIFactory6> factory;
    CHECK(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)));
    ComPtr<IDXGIAdapter1> adapter;
    CHECK(factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter)));
    DXGI_ADAPTER_DESC1 info{};
    CHECK(adapter->GetDesc1(&info));
    if ((info.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
    {
        return DXGI_ERROR_UNSUPPORTED;
    }
    std::memcpy(description, info.Description, sizeof(info.Description));
    description[127] = L'\0';
    *dedicatedBytes = info.DedicatedVideoMemory;
    return S_OK;
}
