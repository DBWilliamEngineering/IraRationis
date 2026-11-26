// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using IraRationis.Core.Hardware;
using IraRationis.Core.Hardware.Models;

namespace IraRationis.Runtime.Tests.Hardware;

internal static class HardwareCapabilityTests
{
    public static void Run()
    {
        ReportSeparatesPhysicalMemoryAndGarbageCollectorBudget();

        ReportPreservesUnavailablePhysicalMemory();
    }

    private static void ReportSeparatesPhysicalMemoryAndGarbageCollectorBudget()
    {
        var capabilities = new HardwareCapabilitySnapshot(Architecture.X64, 6,
            CpuInstructionSets.Sse2 | CpuInstructionSets.Avx2, 64UL << 20, 8UL << 20, 256L << 20);

        using var output = new StringWriter(CultureInfo.GetCultureInfo("fr-FR"));

        HardwareCapabilityReport.WriteTo(output, capabilities);

        var report = output.ToString();

        Require(report.Contains("effectiveProcessors=6;", StringComparison.Ordinal), "Process CPU count");
        Require(report.Contains("usableSimd=Sse2, Avx2;", StringComparison.Ordinal), "Usable instruction sets");
        Require(report.Contains("physicalTotalMiB=64.0;", StringComparison.Ordinal), "Physical memory total");
        Require(report.Contains("physicalAvailableMiB=8.0;", StringComparison.Ordinal), "Physical memory availability");
        Require(report.Contains("gcMemoryBudgetMiB=256.0;", StringComparison.Ordinal), "Separate invariant GC budget");
    }

    private static void ReportPreservesUnavailablePhysicalMemory()
    {
        var capabilities = new HardwareCapabilitySnapshot(Architecture.Arm64, 1,
            CpuInstructionSets.None, null, null, 1024L << 20);

        using var output = new StringWriter(CultureInfo.InvariantCulture);

        HardwareCapabilityReport.WriteTo(output, capabilities);

        var report = output.ToString();

        Require(report.Contains("usableSimd=Scalar;", StringComparison.Ordinal), "Scalar fallback");
        Require(report.Contains("physicalTotalMiB=unavailable;", StringComparison.Ordinal), "Unknown physical total");
        Require(report.Contains("physicalAvailableMiB=unavailable;", StringComparison.Ordinal),
            "GC budget must not substitute for unknown physical availability");
        Require(report.Contains("gcMemoryBudgetMiB=1024.0;", StringComparison.Ordinal), "GC budget remains available");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
