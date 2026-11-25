// SPDX-License-Identifier: GPL-3.0-or-later
// Partial console device ABI, derived from Zettrox/ztrx victory-gpu/src/device.rs and object.rs.
#include "GraphicsBridge.h"
#include <array>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <mutex>
#include <utility>
#include <new>
#include <chrono>
#include <thread>
#include <algorithm>

namespace
{
    using namespace irx;
    struct Device
    {
        void** table;
        UINT bits;
        volatile LONG references;
        UINT64 nullConstantBuffer;
        UINT outOfLine[16];
    };
    static_assert(sizeof(Device) == 0x58);
    static_assert(offsetof(Device, outOfLine) == 0x18);
    ComPtr<IDXGIFactory6> factory;
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<ID3D12Device> host;
    std::mutex gate;
    const GUID DeviceIds[] = {
        __uuidof(ID3D12Device), __uuidof(ID3D12Device1), __uuidof(ID3D12Device2),
        __uuidof(ID3D12Device3), __uuidof(ID3D12Device4), __uuidof(ID3D12Device5),
        __uuidof(ID3D12Device6), __uuidof(ID3D12Device7), __uuidof(ID3D12Device8),
        __uuidof(ID3D12Device11), __uuidof(IUnknown)
    };
    bool Known(const GUID* iid)
    {
        if (!iid)
        {
            return false;
        }
        for (const auto& id : DeviceIds)
        {
            if (IsEqualGUID(*iid, id))
            {
                return true;
            }
        }
        return false;
    }
    ULONG WINAPI AddRef(Device* self)
    {
        return static_cast<ULONG>(InterlockedIncrement(&self->references));
    }
    ULONG WINAPI Release(Device* self)
    {
        // Process-lifetime singleton, as in upstream. Never destroy its table.
        LONG observed = InterlockedCompareExchange(&self->references, 0, 0);
        while (observed > 0)
        {
            const auto prior = InterlockedCompareExchange(&self->references, observed - 1, observed);
            if (prior == observed)
            {
                return static_cast<ULONG>(observed - 1);
            }
            observed = prior;
        }
        return 0;
    }
    HRESULT WINAPI Query(Device* self, const GUID* iid, void** out)
    {
        if (!out)
        {
            return E_POINTER;
        }
        *out = nullptr;
        if (!Known(iid))
        {
            return E_NOINTERFACE;
        }
        *out = self;
        AddRef(self);
        return S_OK;
    }
    HRESULT WINAPI Name(Device*, LPCWSTR name)
    {
        return host->SetName(name);
    }
    UINT WINAPI NodeCount(Device*)
    {
        return host->GetNodeCount();
    }
    UINT WINAPI Increment(Device*, D3D12_DESCRIPTOR_HEAP_TYPE type)
    {
        return host->GetDescriptorHandleIncrementSize(type);
    }
    HRESULT WINAPI Feature(Device*, UINT feature, void* data, UINT size)
    {
        if (!data)
        {
            return E_INVALIDARG;
        }
        if (feature == 0 && size == 64)
        {
            // Console OPTIONS has one additional field at index 11.
            D3D12_FEATURE_DATA_D3D12_OPTIONS options{};
            const auto hr = host->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS, &options, static_cast<UINT>(sizeof(options)));
            if (FAILED(hr))
            {
                return hr;
            }
            static_assert(sizeof(options) == 60);
            UINT values[15]{};
            memcpy(values, &options, static_cast<UINT>(sizeof(options)));
            auto* out = static_cast<UINT*>(data);
            for (UINT i = 0; i < 16; ++i)
            {
                out[i] = i == 11 ? 0 : values[i < 11 ? i : i - 1];
            }
            return S_OK;
        }
        if (feature == 1 && size == 16)
        {
            auto* fields = static_cast<UINT*>(data);
            if (fields[0] != 0)
            {
                return E_INVALIDARG;
            }
            // Logical console topology: one node, non-tiled, UMA, coherent.
            fields[1] = 0;
            fields[2] = 1;
            fields[3] = 1;
            return S_OK;
        }
        // Only queries with checked matching ABI are forwarded.
        if ((feature == 2 && size == 24) ||
            (feature == 3 && size == 12) || (feature == 4 && size == 16))
        {
            return host->CheckFeatureSupport(static_cast<D3D12_FEATURE>(feature), data, size);
        }
        return E_NOTIMPL;
    }
    HRESULT WINAPI GetPrivate(Device*, const GUID* id, UINT* size, void* data)
    {
        return id ? host->GetPrivateData(*id, size, data) : E_INVALIDARG;
    }
    HRESULT WINAPI SetPrivate(Device*, const GUID* id, UINT size, const void* data)
    {
        return id ? host->SetPrivateData(*id, size, data) : E_INVALIDARG;
    }
    // Upstream resource.rs: console heap properties describe the logical
    // unified-memory model, not the physical heap of the Windows adapter.
    // The 20-byte aggregate is returned through the hidden result pointer.
    static_assert(sizeof(D3D12_HEAP_PROPERTIES) == 20);
    D3D12_HEAP_PROPERTIES* WINAPI CustomHeapProperties(
        D3D12_HEAP_PROPERTIES* out, Device*, UINT nodes, D3D12_HEAP_TYPE kind)
    {
        if (!out)
        {
            return nullptr;
        }
        const auto mask = (std::max)(nodes, 1u);
        auto pages = D3D12_CPU_PAGE_PROPERTY_UNKNOWN;
        switch (kind)
        {
        case D3D12_HEAP_TYPE_DEFAULT:
            pages = D3D12_CPU_PAGE_PROPERTY_NOT_AVAILABLE;
            break;
        case D3D12_HEAP_TYPE_UPLOAD:
        case D3D12_HEAP_TYPE_READBACK:
            pages = D3D12_CPU_PAGE_PROPERTY_WRITE_BACK;
            break;
        default:
            break;
        }
        *out = D3D12_HEAP_PROPERTIES{
            D3D12_HEAP_TYPE_CUSTOM, pages,
            pages == D3D12_CPU_PAGE_PROPERTY_UNKNOWN ? D3D12_MEMORY_POOL_UNKNOWN : D3D12_MEMORY_POOL_L0,
            mask, mask};
        return out;
    }
    enum class ObjectKind { Device, QueueOld, QueueNew, Fence, Allocator };
    template<ObjectKind Kind>
    constexpr const char* ObjectName()
    {
        if constexpr (Kind == ObjectKind::Device)
        {
            return "device";
        }
        else if constexpr (Kind == ObjectKind::QueueOld)
        {
            return "queue-old";
        }
        else if constexpr (Kind == ObjectKind::QueueNew)
        {
            return "queue-new";
        }
        else if constexpr (Kind == ObjectKind::Fence)
        {
            return "fence";
        }
        else
        {
            static_assert(Kind == ObjectKind::Allocator);

            return "allocator";
        }
    }
    template<ObjectKind Kind, size_t Slot> HRESULT WINAPI Missing()
    {
        // Unknown return types make returning HRESULT unsafe. Stop at the first unknown slot.
        std::fprintf(stderr, "[graphics] Unsupported console graphics %s slot 0x%zx\n", ObjectName<Kind>(), Slot * sizeof(void*));
        std::fflush(stderr);
        std::abort();
    }
    template<ObjectKind Kind, size_t... Slots> std::array<void*, sizeof...(Slots)> MakeTable(std::index_sequence<Slots...>)
    {
        return {reinterpret_cast<void*>(&Missing<Kind, Slots>)...};
    }
    auto table = MakeTable<ObjectKind::Device>(std::make_index_sequence<160>{});
    Device device{table.data(), 0, 0, 0, {1}};
    // Child object headers retain the console's bookkeeping layout. Host COM
    // objects are stored separately, never exposed as a console object.
    struct Header
    {
        void** table;
        UINT bits;
        volatile LONG refs;
    };
    static_assert(sizeof(Header) == 16);
    struct Queue
    {
        Header header;
        UINT kind;
        D3D12_COMMAND_QUEUE_DESC desc;
        ComPtr<ID3D12CommandQueue> host;
    };
    struct Fence
    {
        Header header;
        volatile LONG64 value;
        UINT flags;
        ComPtr<ID3D12Fence> host;
    };
    struct Allocator
    {
        Header header;
        UINT kind;
        ComPtr<ID3D12CommandAllocator> host;
    };
    static_assert(offsetof(Queue, kind) == 0x10);
    static_assert(offsetof(Fence, value) == 0x10);
    static_assert(offsetof(Allocator, kind) == 0x10);
    std::atomic<bool> newer = false;
    template<class T> ULONG WINAPI ChildAddRef(T* self)
    {
        return static_cast<ULONG>(InterlockedIncrement(&self->header.refs));
    }
    template<class T> ULONG WINAPI ChildRelease(T* self)
    {
        const auto refs = InterlockedDecrement(&self->header.refs);
        if (refs == 0)
        {
            delete self;
        }
        return static_cast<ULONG>(refs);
    }
    template<class T> const GUID& ChildId();
    template<> const GUID& ChildId<Queue>() { return __uuidof(ID3D12CommandQueue); }
    template<> const GUID& ChildId<Fence>() { return __uuidof(ID3D12Fence); }
    template<> const GUID& ChildId<Allocator>() { return __uuidof(ID3D12CommandAllocator); }
    template<class T> bool ChildKnown(const GUID* iid)
    {
        return iid && (IsEqualGUID(*iid, ChildId<T>()) || IsEqualGUID(*iid, __uuidof(ID3D12Pageable)) ||
            IsEqualGUID(*iid, __uuidof(ID3D12DeviceChild)) || IsEqualGUID(*iid, __uuidof(ID3D12Object)) || IsEqualGUID(*iid, __uuidof(IUnknown)));
    }
    template<class T> HRESULT WINAPI ChildQuery(T* self, const GUID* iid, void** out)
    {
        if (!out)
        {
            return E_POINTER;
        }
        *out = nullptr;
        if (!ChildKnown<T>(iid))
        {
            return E_NOINTERFACE;
        }
        *out = self;
        ChildAddRef(self);
        return S_OK;
    }
    template<class T> HRESULT WINAPI ChildName(T* self, LPCWSTR name)
    {
        return self->host->SetName(name);
    }
    template<class T> HRESULT WINAPI ChildGetPrivate(T* self, const GUID* iid, UINT* size, void* data)
    {
        return iid ? self->host->GetPrivateData(*iid, size, data) : E_INVALIDARG;
    }
    template<class T> HRESULT WINAPI ChildSetPrivate(T* self, const GUID* iid, UINT size, const void* data)
    {
        return iid ? self->host->SetPrivateData(*iid, size, data) : E_INVALIDARG;
    }
    template<class T> HRESULT WINAPI Parent(T*, const GUID* iid, void** out)
    {
        return Query(&device, iid, out);
    }
    template<class T, size_t N> void ChildTable(std::array<void*, N>& t)
    {
        t[0] = reinterpret_cast<void*>(&ChildQuery<T>);
        t[1] = reinterpret_cast<void*>(&ChildAddRef<T>);
        t[2] = reinterpret_cast<void*>(&ChildRelease<T>);
        t[3] = reinterpret_cast<void*>(&ChildGetPrivate<T>);
        t[4] = reinterpret_cast<void*>(&ChildSetPrivate<T>);
        t[6] = reinterpret_cast<void*>(&ChildName<T>);
        t[7] = reinterpret_cast<void*>(&Parent<T>);
    }
    // Unlike an Xbox fence, an ordinary D3D12 fence has no CPU-addressable GPU
    // value slot. Direct address access remains unsupported; refresh on query.
    UINT64 WINAPI Completed(Fence* self)
    {
        const auto value = self->host->GetCompletedValue();
        InterlockedExchange64(&self->value, static_cast<LONG64>(value));
        return value;
    }
    HRESULT WINAPI FenceEvent(Fence* self, UINT64 value, HANDLE event)
    {
        return self->host->SetEventOnCompletion(value, event);
    }
    HRESULT WINAPI FenceSignal(Fence* self, UINT64 value)
    {
        const auto hr = self->host->Signal(value);
        if (SUCCEEDED(hr))
        {
            Completed(self);
        }
        return hr;
    }
    UINT WINAPI FenceFlags(Fence* self)
    {
        return self->flags;
    }
    HRESULT WINAPI QueueSignal(Queue* self, Fence* fence, UINT64 value)
    {
        return fence ? self->host->Signal(fence->host.Get(), value) : E_INVALIDARG;
    }
    HRESULT WINAPI QueueWait(Queue* self, Fence* fence, UINT64 value)
    {
        return fence ? self->host->Wait(fence->host.Get(), value) : E_INVALIDARG;
    }
    HRESULT WINAPI QueueFrequency(Queue* self, UINT64* out)
    {
        return out ? self->host->GetTimestampFrequency(out) : E_INVALIDARG;
    }
    // The console queue description uses an explicit out parameter after
    // this, unlike the device's hidden aggregate result pointer.
    void WINAPI QueueDescription(Queue* self, D3D12_COMMAND_QUEUE_DESC* out)
    {
        if (out)
        {
            *out = self->desc;
        }
    }
    HRESULT WINAPI AllocatorReset(Allocator* self)
    {
        return self->host->Reset();
    }
    auto queueOld = MakeTable<ObjectKind::QueueOld>(std::make_index_sequence<36>{});
    auto queueNew = MakeTable<ObjectKind::QueueNew>(std::make_index_sequence<36>{});
    auto fenceTable = MakeTable<ObjectKind::Fence>(std::make_index_sequence<24>{});
    auto allocatorTable = MakeTable<ObjectKind::Allocator>(std::make_index_sequence<24>{});
    HRESULT WINAPI CreateQueue(Device*, const D3D12_COMMAND_QUEUE_DESC* desc, const GUID* iid, void** out)
    {
        if (out)
        {
            *out = nullptr;
        }
        if (!desc)
        {
            return E_INVALIDARG;
        }
        if (!ChildKnown<Queue>(iid))
        {
            return E_NOINTERFACE;
        }
        auto* child = new (std::nothrow) Queue{};
        if (!child)
        {
            return E_OUTOFMEMORY;
        }
        child->header.table = newer.load() ? queueNew.data() : queueOld.data();
        child->header.refs = 1;
        child->kind = static_cast<UINT>(desc->Type);
        child->desc = *desc;
        const auto hr = host->CreateCommandQueue(desc, IID_PPV_ARGS(&child->host));
        if (FAILED(hr) || !out)
        {
            delete child;
            return FAILED(hr) ? hr : S_FALSE;
        }
        *out = child;
        std::fprintf(stderr, "[graphics] Command queue type %u created.\n", child->kind);
        return S_OK;
    }
    struct QueueDescX { UINT kind, flags, engine, queue; };
    HRESULT WINAPI CreateQueueX(Device* self, const QueueDescX* desc, const GUID* iid, void** out)
    {
        if (!desc)
        {
            if (out)
            {
                *out = nullptr;
            }
            return E_INVALIDARG;
        }
        D3D12_COMMAND_QUEUE_DESC d{};
        d.Type = static_cast<D3D12_COMMAND_LIST_TYPE>(desc->kind);
        d.Flags = static_cast<D3D12_COMMAND_QUEUE_FLAGS>(desc->flags);
        d.NodeMask = 1;
        return CreateQueue(self, &d, iid, out);
    }
    HRESULT WINAPI CreateFence(Device*, UINT64 initial, UINT flags, const GUID* iid, void** out)
    {
        if (out)
        {
            *out = nullptr;
        }
        if (!ChildKnown<Fence>(iid))
        {
            return E_NOINTERFACE;
        }
        auto* child = new (std::nothrow) Fence{};
        if (!child)
        {
            return E_OUTOFMEMORY;
        }
        child->header.table = fenceTable.data();
        child->header.refs = 1;
        child->value = static_cast<LONG64>(initial);
        child->flags = flags;
        const auto hr = host->CreateFence(initial, static_cast<D3D12_FENCE_FLAGS>(flags), IID_PPV_ARGS(&child->host));
        if (FAILED(hr) || !out)
        {
            delete child;
            return FAILED(hr) ? hr : S_FALSE;
        }
        *out = child;
        return S_OK;
    }
    HRESULT WINAPI CreateAllocator(Device*, UINT kind, const GUID* iid, void** out)
    {
        if (out)
        {
            *out = nullptr;
        }
        if (!ChildKnown<Allocator>(iid))
        {
            return E_NOINTERFACE;
        }
        auto* child = new (std::nothrow) Allocator{};
        if (!child)
        {
            return E_OUTOFMEMORY;
        }
        child->header.table = allocatorTable.data();
        child->header.refs = 1;
        child->kind = kind;
        const auto hr = host->CreateCommandAllocator(static_cast<D3D12_COMMAND_LIST_TYPE>(kind), IID_PPV_ARGS(&child->host));
        if (FAILED(hr) || !out)
        {
            delete child;
            return FAILED(hr) ? hr : S_FALSE;
        }
        *out = child;
        return S_OK;
    }
    void InitializeTable()
    {
        table[0] = reinterpret_cast<void*>(&Query);
        table[1] = reinterpret_cast<void*>(&AddRef);
        table[2] = reinterpret_cast<void*>(&Release);
        table[3] = reinterpret_cast<void*>(&GetPrivate);
        table[4] = reinterpret_cast<void*>(&SetPrivate);
        table[6] = reinterpret_cast<void*>(&Name);
        table[7] = reinterpret_cast<void*>(&NodeCount);
        table[13] = reinterpret_cast<void*>(&Feature);
        table[15] = reinterpret_cast<void*>(&Increment);
        table[26] = reinterpret_cast<void*>(&CustomHeapProperties); // 0xD0
        table[8] = reinterpret_cast<void*>(&CreateQueue);
        table[9] = reinterpret_cast<void*>(&CreateAllocator);
        table[36] = reinterpret_cast<void*>(&CreateFence);
        table[48] = reinterpret_cast<void*>(&CreateQueueX);
        ChildTable<Queue>(queueOld);
        ChildTable<Queue>(queueNew);
        queueOld[14] = queueNew[16] = reinterpret_cast<void*>(&QueueSignal);
        queueOld[15] = queueNew[17] = reinterpret_cast<void*>(&QueueWait);
        queueOld[16] = queueNew[18] = reinterpret_cast<void*>(&QueueFrequency);
        queueOld[26] = queueNew[28] = reinterpret_cast<void*>(&QueueDescription);
        ChildTable<Fence>(fenceTable);
        fenceTable[8] = reinterpret_cast<void*>(&Completed);
        fenceTable[9] = reinterpret_cast<void*>(&FenceEvent);
        fenceTable[10] = reinterpret_cast<void*>(&FenceSignal);
        fenceTable[11] = reinterpret_cast<void*>(&FenceFlags);
        ChildTable<Allocator>(allocatorTable);
        allocatorTable[8] = reinterpret_cast<void*>(&AllocatorReset);
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI irx_graphics_create(const void*, const UINT* parameters, const GUID* iid, void** out)
{
    if (out)
    {
        *out = nullptr;
    }
    if (!parameters || !iid)
    {
        return E_INVALIDARG;
    }
    if (!Known(iid) || *parameters != 0x00020004)
    {
        return E_NOINTERFACE;
    }
    std::lock_guard lock(gate);
    if (!host)
    {
        const auto hr = irx::CreateHostDevice(factory, adapter, host);
        if (FAILED(hr))
        {
            std::fprintf(stderr, "[graphics] D3D12CreateDevice failed: 0x%08X\n", static_cast<UINT>(hr));
            return hr;
        }
        InitializeTable();
        DXGI_ADAPTER_DESC1 description{};
        if (SUCCEEDED(adapter->GetDesc1(&description)))
        {
            std::fprintf(stderr, "[graphics] Host device created: %ls\n", description.Description);
        }
    }
    if (IsEqualGUID(*iid, __uuidof(ID3D12Device11)))
    {
        newer.store(true);
    }
    return out ? Query(&device, iid, out) : S_FALSE;
}
