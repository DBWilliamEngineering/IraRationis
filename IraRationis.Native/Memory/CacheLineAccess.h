// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <cstdint>
#include <limits>

namespace irx
{
    constexpr std::uintptr_t CacheLineBytes = 64;

    constexpr bool IsCompleteCacheLineInPage(std::uintptr_t address, std::uintptr_t pageSize) noexcept
    {
        if (pageSize < CacheLineBytes || (pageSize & (pageSize - 1)) != 0 ||
            address % CacheLineBytes != 0 ||
            address > (std::numeric_limits<std::uintptr_t>::max)() - CacheLineBytes)
        {
            return false;
        }

        return pageSize - address % pageSize >= CacheLineBytes;
    }

    constexpr bool CanUseResidentPage(bool querySucceeded, bool attributesValid, bool badPage,
                                    std::uint32_t protection) noexcept
    {
        constexpr std::uint32_t ordinaryReadWrite = 0x04;

        return querySucceeded && attributesValid && !badPage && protection == ordinaryReadWrite;
    }
}
