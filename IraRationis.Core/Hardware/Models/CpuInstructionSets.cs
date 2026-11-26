// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Core.Hardware.Models;

[Flags]
public enum CpuInstructionSets
{
    None = 0,
    Sse2 = 1 << 0,
    Sse3 = 1 << 1,
    Ssse3 = 1 << 2,
    Sse41 = 1 << 3,
    Sse42 = 1 << 4,
    Avx = 1 << 5,
    Avx2 = 1 << 6,
    Fma = 1 << 7,
    Avx512F = 1 << 8,
    Avx512BW = 1 << 9,
    Avx512DQ = 1 << 10,
    Avx512VL = 1 << 11,
    AdvSimd = 1 << 12
}
