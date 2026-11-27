// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#ifndef PSAPI_VERSION
#define PSAPI_VERSION 2
#endif

#include <Windows.h>
#include <Psapi.h>
#include "CacheLineAccess.h"
#include "MemoryAccessStatistics.h"

namespace irx
{
    inline std::uintptr_t QueryAccessibleRegionEnd(std::uintptr_t address, bool writable,
                                                   bool* ordinaryPrivate, std::uint64_t* queryCount) noexcept
    {
        if (ordinaryPrivate)
        {
            *ordinaryPrivate = false;
        }

        if (queryCount)
        {
            ++*queryCount;
        }

        MEMORY_BASIC_INFORMATION information{};

        if (!VirtualQuery(reinterpret_cast<void*>(address), &information, sizeof(information)) ||
            information.State != MEM_COMMIT || (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
        {
            return 0;
        }

        const auto protection = information.Protect & 0xff;
        const bool canWrite = protection == PAGE_READWRITE || protection == PAGE_WRITECOPY ||
            protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
        const bool canRead = canWrite || protection == PAGE_READONLY || protection == PAGE_EXECUTE_READ;

        if ((writable && !canWrite) || (!writable && !canRead))
        {
            return 0;
        }

        const auto regionStart = reinterpret_cast<std::uintptr_t>(information.BaseAddress);

        if (information.RegionSize > UINTPTR_MAX - regionStart)
        {
            return 0;
        }

        if (ordinaryPrivate)
        {
            *ordinaryPrivate = information.Type == MEM_PRIVATE && information.Protect == PAGE_READWRITE;
        }

        return regionStart + information.RegionSize;
    }

    inline std::uintptr_t QueryWritableCacheLine(std::uintptr_t address, std::uintptr_t pageSize,
                                                 bool useResidentPageQuery, bool* ordinaryPrivate,
                                                 MemoryAccessStatistics* statistics) noexcept
    {
        if (ordinaryPrivate)
        {
            *ordinaryPrivate = false;
        }

        if (address % CacheLineBytes != 0 || address > UINTPTR_MAX - CacheLineBytes)
        {
            return 0;
        }

        // O(1) requested pages. No permission survives this access; VirtualQuery may scan a large region.
        if (useResidentPageQuery && IsCompleteCacheLineInPage(address, pageSize))
        {
            PSAPI_WORKING_SET_EX_INFORMATION information{};
            information.VirtualAddress = reinterpret_cast<void*>(address);

            if (statistics)
            {
                ++statistics->residentPageQueries;
            }

            const bool querySucceeded = K32QueryWorkingSetEx(GetCurrentProcess(), &information,
                                                             static_cast<DWORD>(sizeof(information))) != FALSE;
            const auto attributes = information.VirtualAttributes;

            if (querySucceeded && attributes.Valid && CanUseResidentPage(true, true, attributes.Bad != 0,
                                                                         static_cast<std::uint32_t>(attributes.
                                                                             Win32Protection)))
            {
                if (statistics)
                {
                    ++statistics->residentPageHits;
                }

                // Page attributes authorize only this line, never private-region batching.
                return address + CacheLineBytes;
            }
        }

        return QueryAccessibleRegionEnd(address, true, ordinaryPrivate,
                                        statistics ? &statistics->destinationRegionQueries : nullptr);
    }
}
