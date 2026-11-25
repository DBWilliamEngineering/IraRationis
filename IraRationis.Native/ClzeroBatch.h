// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once
#include <cstdint>

namespace irx
{
    // Count only complete cache lines inside the validated destination region.
    // The caller executes the original ADD/CMP/JB after the final zeroed line.
    constexpr std::uint64_t ClzeroBatchCount(std::uint64_t address, std::uint64_t limit,
                                             std::uint64_t regionEnd) noexcept
    {
        constexpr std::uint64_t maximumLines = 1024;
        const auto line = address & ~UINT64_C(63);
        if (regionEnd <= line || regionEnd - line < 64)
        {
            return 0;
        }
        if (address >= limit || address > UINT64_MAX - 64)
        {
            return 1;
        }
        const auto remaining = limit - address;
        const auto requested = (remaining - 1) / 64 + 1;
        const auto available = (regionEnd - line) / 64;
        // Leave the final addition to the original loop tail, including wraparound.
        const auto beforeWrap = (UINT64_MAX - address) / 64 + 1;
        auto count = requested < available ? requested : available;
        count = count < beforeWrap ? count : beforeWrap;
        return count < maximumLines ? count : maximumLines;
    }

    static_assert(ClzeroBatchCount(0x1000, 0x2000, 0x3000) == 64);
    static_assert(ClzeroBatchCount(0x1001, 0x1082, 0x3000) == 3);
    static_assert(ClzeroBatchCount(0x1000, 0x1000, 0x3000) == 1);
    static_assert(ClzeroBatchCount(0x1000, 0x3000, 0x1080) == 2);
    static_assert(ClzeroBatchCount(0x1000, 0x3000, 0x103f) == 0);
    static_assert(ClzeroBatchCount(0x1000, 0x100000, 0x100000) == 1024);
    static_assert(ClzeroBatchCount(UINT64_MAX - 127, UINT64_MAX, UINT64_MAX) == 1);
}
