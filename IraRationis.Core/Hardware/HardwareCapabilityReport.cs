// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using IraRationis.Core.Hardware.Models;

namespace IraRationis.Core.Hardware;

public static class HardwareCapabilityReport
{
    public static void WriteTo(TextWriter writer, HardwareCapabilitySnapshot capabilities)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(capabilities);

        var instructionSets = capabilities.UsableInstructionSets == CpuInstructionSets.None
            ? "Scalar"
            : capabilities.UsableInstructionSets.ToString();

        writer.WriteLine(FormattableString.Invariant(
            $"[hardware] processArchitecture={capabilities.ProcessArchitecture}; effectiveProcessors={capabilities.EffectiveProcessorCount}; usableSimd={instructionSets}; scalarFallback=True;"));

        writer.WriteLine(
            $"[hardware] physicalTotalMiB={FormatPhysicalMemory(capabilities.TotalPhysicalMemoryBytes)}; physicalAvailableMiB={FormatPhysicalMemory(capabilities.AvailablePhysicalMemoryBytes)}; gcMemoryBudgetMiB={FormatMebibytes((ulong)capabilities.GarbageCollectorMemoryBudgetBytes)}; memorySnapshot=True;");
    }

    private static string FormatPhysicalMemory(ulong? bytes)
    {
        return bytes.HasValue ? FormatMebibytes(bytes.Value) : "unavailable";
    }

    private static string FormatMebibytes(ulong bytes)
    {
        return (bytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture);
    }
}
