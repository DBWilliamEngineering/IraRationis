// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Core.Hardware.Models;

public sealed record HardwareCapabilitySnapshot
{
    public HardwareCapabilitySnapshot(Architecture processArchitecture, int effectiveProcessorCount,
        CpuInstructionSets usableInstructionSets, ulong? totalPhysicalMemoryBytes,
        ulong? availablePhysicalMemoryBytes, long garbageCollectorMemoryBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveProcessorCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(garbageCollectorMemoryBudgetBytes);

        ProcessArchitecture = processArchitecture;
        EffectiveProcessorCount = effectiveProcessorCount;
        UsableInstructionSets = usableInstructionSets;
        TotalPhysicalMemoryBytes = totalPhysicalMemoryBytes;
        AvailablePhysicalMemoryBytes = availablePhysicalMemoryBytes;
        GarbageCollectorMemoryBudgetBytes = garbageCollectorMemoryBudgetBytes;
    }

    public Architecture ProcessArchitecture
    {
        get;
    }

    public int EffectiveProcessorCount
    {
        get;
    }

    public CpuInstructionSets UsableInstructionSets
    {
        get;
    }

    public ulong? TotalPhysicalMemoryBytes
    {
        get;
    }

    public ulong? AvailablePhysicalMemoryBytes
    {
        get;
    }

    public long GarbageCollectorMemoryBudgetBytes
    {
        get;
    }
}
