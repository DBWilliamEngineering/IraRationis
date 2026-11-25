// SPDX-License-Identifier: GPL-3.0-or-later
// Synthetic frame upload and presentation test; not a title renderer.
#include "GraphicsBridge.h"
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <thread>
#include <cstdint>

namespace
{
    using namespace irx;
    constexpr UINT Width = 1280, Height = 720, Count = 2;
    using Clock = std::chrono::steady_clock;

    LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM w, LPARAM l)
    {
        if (message == WM_CLOSE)
        {
            DestroyWindow(window);
            return 0;
        }
        return DefWindowProcW(window, message, w, l);
    }

    struct Window
    {
        HWND handle = nullptr;

        ~Window()
        {
            if (IsWindow(handle))
            {
                DestroyWindow(handle);
            }
            UnregisterClassW(L"IrxPresentationProbe", GetModuleHandleW(nullptr));
        }

        HRESULT Create()
        {
            const auto instance = GetModuleHandleW(nullptr);
            WNDCLASSW cls{};
            cls.lpfnWndProc = WindowProc;
            cls.hInstance = instance;
            cls.lpszClassName = L"IrxPresentationProbe";
            cls.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
            if (!RegisterClassW(&cls) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }
            RECT rect{0, 0, static_cast<LONG>(Width), static_cast<LONG>(Height)};
            constexpr DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
            AdjustWindowRect(&rect, style, FALSE);
            handle = CreateWindowExW(0, cls.lpszClassName, L"Ira Rationis | First Light | Synthetic frame probe", style,
                                     CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top,
                                     nullptr, nullptr, instance, nullptr);
            if (!handle)
            {
                return HRESULT_FROM_WIN32(GetLastError());
            }
            ShowWindow(handle, SW_SHOW);
            return S_OK;
        }
    };

    struct Event
    {
        HANDLE handle = CreateEventW(nullptr, FALSE, FALSE, nullptr);

        ~Event()
        {
            if (handle)
            {
                CloseHandle(handle);
            }
        }
    };

    struct Frame
    {
        ComPtr<ID3D12Resource> target, upload;
        ComPtr<ID3D12CommandAllocator> allocator;
        unsigned char* data = nullptr;
        UINT64 fence = 0;
    };

    void Wait(ID3D12Fence* fence, UINT64 value, HANDLE event)
    {
        if (fence->GetCompletedValue() == UINT64_MAX)
        {
            std::abort();
        }
        if (fence->GetCompletedValue() < value)
        {
            if (FAILED(fence->SetEventOnCompletion(value, event)) || WaitForSingleObject(event, 10000) != WAIT_OBJECT_0)
            {
                std::fprintf(stderr, "[present] GPU fence failed or timed out.\n");
                std::abort(); // never release resources still in flight
            }
        }
    }

    D3D12_RESOURCE_BARRIER Barrier(ID3D12Resource* target, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
    {
        D3D12_RESOURCE_BARRIER barrier{};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Transition.pResource = target;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = from;
        barrier.Transition.StateAfter = to;
        return barrier;
    }

    // A synthetic moving frame, using full sequential writes and no read-modify-write of mapped VRAM.
    void Fill(Frame& frame, UINT pitch, UINT number)
    {
        for (UINT y = 0; y < Height; ++y)
        {
            auto* row = reinterpret_cast<UINT*>(frame.data + static_cast<size_t>(y) * pitch);
            for (UINT x = 0; x < Width; ++x)
            {
                const auto r = (x + number * 4u) & 255u;
                const auto g = (y + number * 2u) & 255u;
                const auto b = ((x / 64u + y / 64u + number / 8u) & 1u) ? 220u : 30u;
                row[x] = 0xff000000u | (b << 16) | (g << 8) | r;
            }
        }
    }

    HRESULT Run(UINT seconds, UINT fps, bool gpuUpload)
    {
        ComPtr<IDXGIFactory6> factory;
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<ID3D12Device> device;
        auto hr = CreateHostDevice(factory, adapter, device);
        if (FAILED(hr))
        {
            return hr;
        }
        DXGI_ADAPTER_DESC1 description{};
        if (SUCCEEDED(adapter->GetDesc1(&description)))
        {
            std::printf("[present] Adapter: %ls\n", description.Description);
        }
        if (gpuUpload)
        {
            D3D12_FEATURE_DATA_D3D12_OPTIONS16 options{};
            if (FAILED(device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS16, &options,
                                                   static_cast<UINT>(sizeof(options)))) || !options.
                GPUUploadHeapSupported)
            {
                return DXGI_ERROR_UNSUPPORTED;
            }
        }
        Window window;
        hr = window.Create();
        if (FAILED(hr))
        {
            return hr;
        }
        ComPtr<ID3D12CommandQueue> queue;
        D3D12_COMMAND_QUEUE_DESC q{};
        q.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        hr = device->CreateCommandQueue(&q, IID_PPV_ARGS(&queue));
        if (FAILED(hr))
        {
            return hr;
        }
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = Width;
        desc.Height = Height;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = Count;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        ComPtr<IDXGISwapChain1> initial;
        hr = factory->CreateSwapChainForHwnd(queue.Get(), window.handle, &desc, nullptr, nullptr, &initial);
        if (FAILED(hr))
        {
            return hr;
        }
        ComPtr<IDXGISwapChain3> swap;
        hr = initial.As(&swap);
        if (FAILED(hr))
        {
            return hr;
        }
        std::array<Frame, Count> frames{};
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
        UINT64 bytes = 0;
        for (UINT i = 0; i < Count; ++i)
        {
            auto& frame = frames[i];
            hr = swap->GetBuffer(i, IID_PPV_ARGS(&frame.target));
            if (FAILED(hr))
            {
                return hr;
            }
            auto resource = frame.target->GetDesc();
            device->GetCopyableFootprints(&resource, 0, 1, 0, &footprint, nullptr, nullptr, &bytes);
            D3D12_HEAP_PROPERTIES heap{};
            heap.Type = gpuUpload ? D3D12_HEAP_TYPE_GPU_UPLOAD : D3D12_HEAP_TYPE_UPLOAD;
            heap.CreationNodeMask = heap.VisibleNodeMask = 1;
            D3D12_RESOURCE_DESC buffer{};
            buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            buffer.Width = bytes;
            buffer.Height = 1;
            buffer.DepthOrArraySize = buffer.MipLevels = 1;
            buffer.SampleDesc.Count = 1;
            buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            hr = device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer,
                                                 D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                                 IID_PPV_ARGS(&frame.upload));
            if (FAILED(hr))
            {
                return hr;
            }
            D3D12_RANGE noRead{0, 0};
            void* mapped = nullptr;
            hr = frame.upload->Map(0, &noRead, &mapped);
            if (FAILED(hr))
            {
                return hr;
            }
            frame.data = static_cast<unsigned char*>(mapped);
            hr = device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&frame.allocator));
            if (FAILED(hr))
            {
                return hr;
            }
        }
        ComPtr<ID3D12GraphicsCommandList> list;
        hr = device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, frames[0].allocator.Get(), nullptr,
                                       IID_PPV_ARGS(&list));
        if (FAILED(hr))
        {
            return hr;
        }
        hr = list->Close();
        if (FAILED(hr))
        {
            return hr;
        }
        ComPtr<ID3D12Fence> fence;
        hr = device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
        if (FAILED(hr))
        {
            return hr;
        }
        Event event;
        if (!event.handle)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }
        UINT64 serial = 0;
        UINT count = 0, late = 0;
        double fillMs = 0, waitMs = 0, presentMs = 0;
        const auto start = Clock::now();
        auto deadline = start;
        const auto period = std::chrono::nanoseconds(static_cast<std::int64_t>(1000000000ull / fps));
        while (IsWindow(window.handle) && Clock::now() - start < std::chrono::seconds(seconds))
        {
            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (!IsWindow(window.handle))
            {
                break;
            }
            auto& frame = frames[swap->GetCurrentBackBufferIndex()];
            auto tick = Clock::now();
            Wait(fence.Get(), frame.fence, event.handle);
            waitMs += std::chrono::duration<double, std::milli>(Clock::now() - tick).count();
            tick = Clock::now();
            Fill(frame, footprint.Footprint.RowPitch, count);
            // Drain write-combining stores before submission.
            MemoryBarrier();
            fillMs += std::chrono::duration<double, std::milli>(Clock::now() - tick).count();
            hr = frame.allocator->Reset();
            if (FAILED(hr))
            {
                break;
            }
            hr = list->Reset(frame.allocator.Get(), nullptr);
            if (FAILED(hr))
            {
                break;
            }
            auto barrier = Barrier(frame.target.Get(), D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_DEST);
            list->ResourceBarrier(1, &barrier);
            D3D12_TEXTURE_COPY_LOCATION src{}, dst{};
            src.pResource = frame.upload.Get();
            src.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
            src.PlacedFootprint = footprint;
            dst.pResource = frame.target.Get();
            dst.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
            list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
            barrier = Barrier(frame.target.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PRESENT);
            list->ResourceBarrier(1, &barrier);
            hr = list->Close();
            if (FAILED(hr))
            {
                break;
            }
            ID3D12CommandList* lists[]{list.Get()};
            queue->ExecuteCommandLists(1, lists);
            tick = Clock::now();
            hr = swap->Present(1, 0);
            presentMs += std::chrono::duration<double, std::milli>(Clock::now() - tick).count();
            frame.fence = ++serial;
            if (FAILED(queue->Signal(fence.Get(), serial)))
            {
                std::abort();
            }
            if (FAILED(hr) || hr == DXGI_STATUS_OCCLUDED)
            {
                break;
            }
            ++count;
            deadline += period;
            if (Clock::now() < deadline)
            {
                std::this_thread::sleep_until(deadline);
            }
            else
            {
                ++late;
                if (Clock::now() - deadline > period)
                {
                    deadline = Clock::now();
                }
            }
        }
        Wait(fence.Get(), serial, event.handle);
        const auto elapsed = std::chrono::duration<double>(Clock::now() - start).count();
        const auto divisor = static_cast<double>(count ? count : 1);
        std::printf(
            "[present] %s frames=%u elapsed=%.3fs submit_fps=%.2f late=%u fill_ms=%.3f fence_ms=%.3f present_ms=%.3f\n",
            gpuUpload ? "GPU_UPLOAD" : "UPLOAD", count, elapsed, static_cast<double>(count) / elapsed, late,
            fillMs / divisor, waitMs / divisor, presentMs / divisor);
        std::fflush(stdout);
        if (SUCCEEDED(hr) && count == 0)
        {
            return E_FAIL;
        }
        return hr;
    }
}

extern "C" __declspec(dllexport) HRESULT WINAPI irx_present_probe(UINT seconds, UINT fps, UINT gpuUpload)
{
    if (seconds < 1 || seconds > 120 || fps < 1 || fps > 240 || gpuUpload > 1)
    {
        return E_INVALIDARG;
    }
    return Run(seconds, fps, gpuUpload != 0);
}
