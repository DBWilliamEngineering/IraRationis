// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Device.Hardware.Models;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics.Device.Hardware;

public static unsafe class HostGraphicsCapabilities
{
    private static HostGraphicsCapabilitySnapshot? _current;

    public static HostGraphicsCapabilitySnapshot? Current
    {
        get
        {
            return Volatile.Read(ref _current);
        }
    }

    internal static HostGraphicsCapabilitySnapshot Capture(nint selectedAdapter, string adapterName,
        in DXGI_ADAPTER_DESC1 description, uint raytracingTierProbeResult, uint shaderModel65ProbeResult)
    {
        var queryResult = QueryLocalMemoryBudget(selectedAdapter, out var localMemoryBudget);

        var capabilities = new HostGraphicsCapabilitySnapshot
        {
            AdapterName = adapterName,
            VendorId = description.VendorId,
            DeviceId = description.DeviceId,
            DedicatedVideoMemoryBytes = (ulong)description.DedicatedVideoMemory,
            DedicatedSystemMemoryBytes = (ulong)description.DedicatedSystemMemory,
            SharedSystemMemoryBytes = (ulong)description.SharedSystemMemory,
            LocalMemoryBudget = localMemoryBudget,
            LocalMemoryQueryResult = queryResult,
            RaytracingTierProbeResult = raytracingTierProbeResult,
            ShaderModel65ProbeResult = shaderModel65ProbeResult,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };

        Volatile.Write(ref _current, capabilities);

        return capabilities;
    }

    private static int QueryLocalMemoryBudget(nint selectedAdapter, out GraphicsMemoryBudgetSnapshot? memoryBudget)
    {
        const int pointerError = unchecked((int)0x80004003);

        memoryBudget = null;

        if (selectedAdapter == 0)
        {
            return pointerError;
        }

        var adapterId = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");
        void* adapterInterface = null;

        var queryResult = (int)((IDXGIAdapter1*)selectedAdapter)->QueryInterface(&adapterId, &adapterInterface);

        if (queryResult < 0)
        {
            return queryResult;
        }

        if (adapterInterface == null)
        {
            return pointerError;
        }

        var budgetAdapter = (IDXGIAdapter3*)adapterInterface;

        try
        {
            var memoryInformation = default(DXGI_QUERY_VIDEO_MEMORY_INFO);

            queryResult = (int)budgetAdapter->QueryVideoMemoryInfo(0,
                DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &memoryInformation);

            if (queryResult >= 0)
            {
                memoryBudget = new GraphicsMemoryBudgetSnapshot(memoryInformation.Budget,
                    memoryInformation.CurrentUsage);
            }

            return queryResult;
        }
        finally
        {
            _ = budgetAdapter->Release();
        }
    }
}
