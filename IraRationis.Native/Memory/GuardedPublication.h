// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <Windows.h>
#include <Psapi.h>
#include <cstdint>
#include "CacheLineAccess.h"

namespace irx
{
    // A fresh resident-page query authorizes only this aligned scalar write.
    // The caller retains its original kernel-checked fallback and fence handling.
    inline bool TryPublishResidentScalar(std::uintptr_t address, std::uint64_t value, unsigned bytes) noexcept
    {
        if ((bytes != 4 && bytes != 8) || address == 0 || address % bytes != 0 || address > UINTPTR_MAX - bytes)
        {
            return false;
        }
        PSAPI_WORKING_SET_EX_INFORMATION page{};
        page.VirtualAddress = reinterpret_cast<void*>(address);
        if (!K32QueryWorkingSetEx(GetCurrentProcess(), &page, static_cast<DWORD>(sizeof(page))))
        {
            return false;
        }
        const auto attributes = page.VirtualAttributes;
        if (!CanUseResidentPage(true, attributes.Valid != 0, attributes.Bad != 0,
            static_cast<std::uint32_t>(attributes.Win32Protection)))
        {
            return false;
        }
        __try
        {
            if (bytes == 4)
            {
                InterlockedExchange(reinterpret_cast<volatile LONG*>(address), static_cast<LONG>(value));
            }
            else
            {
                InterlockedExchange64(reinterpret_cast<volatile LONG64*>(address), static_cast<LONG64>(value));
            }
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // The mapping may have changed after the query. Never report success.
            return false;
        }
    }
}
