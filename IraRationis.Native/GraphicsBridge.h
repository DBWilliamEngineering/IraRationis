// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

namespace irx
{
    using Microsoft::WRL::ComPtr;

    inline HRESULT CreateHostDevice(ComPtr<IDXGIFactory6>& factory, ComPtr<IDXGIAdapter1>& adapter,
                                    ComPtr<ID3D12Device>& device)
    {
        auto hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
        if (FAILED(hr))
        {
            return hr;
        }
        for (UINT index = 0; ; ++index)
        {
            hr = factory->EnumAdapterByGpuPreference(index, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                                                     IID_PPV_ARGS(&adapter));
            if (FAILED(hr))
            {
                return hr;
            }
            DXGI_ADAPTER_DESC1 description{};
            hr = adapter->GetDesc1(&description);
            if (SUCCEEDED(hr) && !(description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE))
            {
                hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_12_0, IID_PPV_ARGS(&device));
                if (SUCCEEDED(hr))
                {
                    return hr;
                }
            }
            adapter.Reset();
        }
    }
}
