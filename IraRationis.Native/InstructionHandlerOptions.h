// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <cstdint>

namespace irx
{
    struct InstructionHandlerOptions
    {
        std::uint32_t size;
        std::uint32_t flags;
    };

    static_assert(sizeof(InstructionHandlerOptions) == 8);

    constexpr std::uint32_t BatchClzero = 1;
    constexpr std::uint32_t ProfileInstructions = 2;
    constexpr std::uint32_t ResidentPageQueries = 4;
    constexpr std::uint32_t InstructionHandlerFlags = BatchClzero | ProfileInstructions | ResidentPageQueries;
}
