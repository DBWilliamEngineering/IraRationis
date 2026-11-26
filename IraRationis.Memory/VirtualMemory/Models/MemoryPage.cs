// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Memory.VirtualMemory.Models;

public readonly record struct MemoryPage(nuint CpuAddress, ulong GpuAddress, MemoryResidency Residency);
