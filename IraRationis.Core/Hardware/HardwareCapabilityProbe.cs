// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using IraRationis.Core.Hardware.Models;

namespace IraRationis.Core.Hardware;

public static class HardwareCapabilityProbe
{
    public static HardwareCapabilitySnapshot Capture()
    {
        var hasPhysicalMemory = PhysicalMemoryProbe.TryCapture(out var memoryStatus);
        var garbageCollectorMemory = GC.GetGCMemoryInfo();

        return new HardwareCapabilitySnapshot(RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount, CaptureUsableInstructionSets(),
            hasPhysicalMemory ? memoryStatus.TotalPhysicalBytes : null,
            hasPhysicalMemory ? memoryStatus.AvailablePhysicalBytes : null,
            garbageCollectorMemory.TotalAvailableMemoryBytes);
    }

    private static CpuInstructionSets CaptureUsableInstructionSets()
    {
        // Runtime checks include OS/runtime availability; CPUID alone does not.
        return Include(Sse2.IsSupported, CpuInstructionSets.Sse2)
               | Include(Sse3.IsSupported, CpuInstructionSets.Sse3)
               | Include(Ssse3.IsSupported, CpuInstructionSets.Ssse3)
               | Include(Sse41.IsSupported, CpuInstructionSets.Sse41)
               | Include(Sse42.IsSupported, CpuInstructionSets.Sse42)
               | Include(Avx.IsSupported, CpuInstructionSets.Avx)
               | Include(Avx2.IsSupported, CpuInstructionSets.Avx2)
               | Include(Fma.IsSupported, CpuInstructionSets.Fma)
               | Include(Avx512F.IsSupported, CpuInstructionSets.Avx512F)
               | Include(Avx512BW.IsSupported, CpuInstructionSets.Avx512BW)
               | Include(Avx512DQ.IsSupported, CpuInstructionSets.Avx512DQ)
               | Include(Avx512F.VL.IsSupported, CpuInstructionSets.Avx512VL)
               | Include(AdvSimd.IsSupported, CpuInstructionSets.AdvSimd);
    }

    private static CpuInstructionSets Include(bool supported, CpuInstructionSets instructionSet)
    {
        return supported ? instructionSet : CpuInstructionSets.None;
    }
}
