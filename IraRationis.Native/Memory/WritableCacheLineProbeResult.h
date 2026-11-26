// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <cstdint>

namespace irx
{
    struct WritableCacheLineProbeResult
    {
        std::uint32_t size;
        std::uint32_t cleared;
        std::uint32_t residentPageQueries;
        std::uint32_t residentPageHits;
        std::uint32_t regionQueries;
        std::uint32_t accessFaulted;
        std::uint64_t validatedEnd;
    };

    static_assert(sizeof(WritableCacheLineProbeResult) == 32);
}
