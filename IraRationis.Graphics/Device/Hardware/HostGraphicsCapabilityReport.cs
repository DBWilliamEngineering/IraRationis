// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using IraRationis.Graphics.Device.Hardware.Models;

namespace IraRationis.Graphics.Device.Hardware;

public static class HostGraphicsCapabilityReport
{
    public static void WriteTo(TextWriter writer, HostGraphicsCapabilitySnapshot capabilities)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(capabilities);

        writer.WriteLine(FormattableString.Invariant(
            $"[hardware] gpuAdapter={capabilities.AdapterName}; vendor=0x{capabilities.VendorId:X4}; device=0x{capabilities.DeviceId:X4}; dedicatedVideoMiB={FormatMebibytes(capabilities.DedicatedVideoMemoryBytes)}; dedicatedSystemMiB={FormatMebibytes(capabilities.DedicatedSystemMemoryBytes)}; sharedSystemMiB={FormatMebibytes(capabilities.SharedSystemMemoryBytes)};"));

        writer.WriteLine(FormattableString.Invariant(
            $"[hardware] raytracingTierProbe={capabilities.RaytracingTierProbeResult}; shaderModel65Probe=0x{capabilities.ShaderModel65ProbeResult:X}; shaderModelQueryCeiling=6.5;"));

        WriteMemoryBudget(writer, capabilities);
    }

    private static void WriteMemoryBudget(TextWriter writer, HostGraphicsCapabilitySnapshot capabilities)
    {
        var memoryBudget = capabilities.LocalMemoryBudget;

        if (memoryBudget is null)
        {
            writer.WriteLine(FormattableString.Invariant(
                $"[hardware] gpuLocalBudget=unavailable; queryHResult=0x{unchecked((uint)capabilities.LocalMemoryQueryResult):X8}; node=0; capturedAtUtc={capabilities.CapturedAtUtc:O};"));

            return;
        }

        writer.WriteLine(FormattableString.Invariant(
            $"[hardware] gpuLocalBudgetMiB={FormatMebibytes(memoryBudget.BudgetBytes)}; gpuLocalUsageMiB={FormatMebibytes(memoryBudget.CurrentUsageBytes)}; gpuLocalHeadroomMiB={FormatMebibytes(memoryBudget.HeadroomBytes)}; node=0; budgetSnapshot=True; capturedAtUtc={capabilities.CapturedAtUtc:O};"));
    }

    private static string FormatMebibytes(ulong bytes)
    {
        return (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
    }
}
