// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Device.Hardware.Models;

public sealed record HostGraphicsCapabilitySnapshot
{
    public required string AdapterName
    {
        get;
        init;
    }

    public required uint VendorId
    {
        get;
        init;
    }

    public required uint DeviceId
    {
        get;
        init;
    }

    public required ulong DedicatedVideoMemoryBytes
    {
        get;
        init;
    }

    public required ulong DedicatedSystemMemoryBytes
    {
        get;
        init;
    }

    public required ulong SharedSystemMemoryBytes
    {
        get;
        init;
    }

    public required GraphicsMemoryBudgetSnapshot? LocalMemoryBudget
    {
        get;
        init;
    }

    public required int LocalMemoryQueryResult
    {
        get;
        init;
    }

    public required uint RaytracingTierProbeResult
    {
        get;
        init;
    }

    public required uint ShaderModel65ProbeResult
    {
        get;
        init;
    }

    public required DateTimeOffset CapturedAtUtc
    {
        get;
        init;
    }
}
