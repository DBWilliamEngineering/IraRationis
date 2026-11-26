// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <cstdint>

namespace irx
{
    struct MemoryAccessStatistics
    {
        std::uint64_t instructionRegionQueries = 0;
        std::uint64_t destinationRegionQueries = 0;
        std::uint64_t residentPageQueries = 0;
        std::uint64_t residentPageHits = 0;
    };
}
