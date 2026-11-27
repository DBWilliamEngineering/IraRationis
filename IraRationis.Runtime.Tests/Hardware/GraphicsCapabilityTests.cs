// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using IraRationis.Graphics.Device.Hardware;
using IraRationis.Graphics.Device.Hardware.Models;

namespace IraRationis.Runtime.Tests.Hardware;

internal static class GraphicsCapabilityTests
{
    public static void Run()
    {
        MemoryHeadroomSaturatesAtZero();

        ReportKeepsUnknownBudgetUnavailable();
    }

    private static void MemoryHeadroomSaturatesAtZero()
    {
        Require(new GraphicsMemoryBudgetSnapshot(100, 40).HeadroomBytes == 60, "Headroom below budget");
        Require(new GraphicsMemoryBudgetSnapshot(100, 100).HeadroomBytes == 0, "No headroom at budget");
        Require(new GraphicsMemoryBudgetSnapshot(100, 101).HeadroomBytes == 0, "Over-budget usage does not wrap");
        Require(new GraphicsMemoryBudgetSnapshot(ulong.MaxValue, 0).HeadroomBytes == ulong.MaxValue,
            "Full-width memory counters");
    }

    private static void ReportKeepsUnknownBudgetUnavailable()
    {
        var capabilities = new HostGraphicsCapabilitySnapshot
        {
            AdapterName = "Synthetic adapter",
            VendorId = 0x1234,
            DeviceId = 0x5678,
            DedicatedVideoMemoryBytes = 24UL << 30,
            DedicatedSystemMemoryBytes = 0,
            SharedSystemMemoryBytes = 16UL << 30,
            LocalMemoryBudget = null,
            LocalMemoryQueryResult = unchecked((int)0x80004002),
            RaytracingTierProbeResult = 0,
            ShaderModel65ProbeResult = 0x65,
            CapturedAtUtc = DateTimeOffset.UnixEpoch
        };

        using var output = new StringWriter(CultureInfo.GetCultureInfo("fr-FR"));

        HostGraphicsCapabilityReport.WriteTo(output, capabilities);

        var report = output.ToString();

        Require(report.Contains("dedicatedVideoMiB=24576.0;", StringComparison.Ordinal), "Physical capacity reported");
        Require(report.Contains("gpuLocalBudget=unavailable;", StringComparison.Ordinal), "Failed query stays unavailable");
        Require(report.Contains("queryHResult=0x80004002;", StringComparison.Ordinal), "Query failure preserved");
        Require(!report.Contains("gpuLocalHeadroomMiB=", StringComparison.Ordinal), "Capacity cannot replace headroom");
        Require(report.Contains("shaderModelQueryCeiling=6.5;", StringComparison.Ordinal), "Bounded shader model probe");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
