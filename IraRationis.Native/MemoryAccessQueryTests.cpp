// SPDX-License-Identifier: GPL-3.0-or-later

#include "Memory/CacheLineAccess.h"
#include "Memory/GuardedPublication.h"
#include "Memory/WritableCacheLineProbeResult.h"
#include "InstructionHandlerOptions.h"
#include <cstddef>
#include <cstdio>
#include <limits>

constexpr auto MaximumAddress = (std::numeric_limits<std::uintptr_t>::max)();

static_assert(irx::CacheLineBytes == 64, "CLZERO must retain its 64-byte line size.");

static_assert(irx::IsCompleteCacheLineInPage(0x1000, 4096),
    "The first line of a page must fit.");
static_assert(irx::IsCompleteCacheLineInPage(0x1040, 4096),
    "An interior aligned line must fit.");
static_assert(irx::IsCompleteCacheLineInPage(0x1fc0, 4096),
    "The final complete line of a page must fit without inspecting the next page.");
static_assert(irx::IsCompleteCacheLineInPage(0x1000, 64),
    "A page exactly one cache line wide must fit.");
static_assert(irx::IsCompleteCacheLineInPage(0x1fffc0, 2 * 1024 * 1024),
    "The final line of a larger power-of-two page must fit.");
static_assert(!irx::IsCompleteCacheLineInPage(0x1001, 4096),
    "An unaligned input must not authorize a cache-line write.");
static_assert(!irx::IsCompleteCacheLineInPage(0x1fff, 4096),
    "An unaligned range crossing a page boundary must be rejected.");
static_assert(!irx::IsCompleteCacheLineInPage(0x1000, 0),
    "An uninitialized page size must be rejected.");
static_assert(!irx::IsCompleteCacheLineInPage(0x1000, 32),
    "A page smaller than one cache line must be rejected.");
static_assert(!irx::IsCompleteCacheLineInPage(0x1000, 96),
    "A page size not divisible by a complete line must be rejected.");
static_assert(!irx::IsCompleteCacheLineInPage(0x1000, 6144),
    "A non-power-of-two page size must be rejected even when divisible by 64.");
static_assert(irx::IsCompleteCacheLineInPage(MaximumAddress - 127, 4096),
    "A complete line with a representable exclusive end must fit near the address limit.");
static_assert(!irx::IsCompleteCacheLineInPage(MaximumAddress - 63, 4096),
    "The final address-space line must be rejected when its exclusive end overflows.");
static_assert(!irx::IsCompleteCacheLineInPage(MaximumAddress, 4096),
    "The maximum unaligned address must be rejected.");

static_assert(irx::CanUseResidentPage(true, true, false, 0x04),
    "Successful valid ordinary read/write page metadata must permit the fast path.");
static_assert(!irx::CanUseResidentPage(false, true, false, 0x04),
    "Failed queries must fall back even when their output resembles valid metadata.");
static_assert(!irx::CanUseResidentPage(true, false, false, 0x04),
    "Invalid page attributes must fall back without trusting their protection bits.");
static_assert(!irx::CanUseResidentPage(true, true, true, 0x04),
    "A page reported as bad must not use the fast path.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x00),
    "Missing protection must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x01),
    "No-access pages must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x02),
    "Read-only pages must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x08),
    "Copy-on-write pages must preserve the existing validation path.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x10),
    "Execute-only pages must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x20),
    "Execute/read pages must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x40),
    "Executable read/write pages must preserve the existing validation path.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x80),
    "Executable copy-on-write pages must preserve the existing validation path.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x104),
    "Guarded read/write pages must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x200),
    "A no-cache modifier alone must not imply write permission.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x204),
    "Uncached read/write pages must preserve the existing validation path.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x400),
    "A write-combine modifier alone must not imply write permission.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x404),
    "Write-combined read/write pages must preserve the existing validation path.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x704),
    "Combined page modifiers must not be stripped before accepting protection.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x06),
    "An invalid combination of base protections must fall back.");
static_assert(!irx::CanUseResidentPage(true, true, false, 0x1004),
    "Unknown protection bits must fall back.");

static_assert(offsetof(irx::InstructionHandlerOptions, size) == 0,
    "The managed options size field must remain at offset zero.");
static_assert(offsetof(irx::InstructionHandlerOptions, flags) == 4,
    "The managed options flags field must remain at offset four.");
static_assert(irx::BatchClzero == 1, "The managed batching option must retain bit zero.");
static_assert(irx::ProfileInstructions == 2, "The managed profiling option must retain bit one.");
static_assert(irx::ResidentPageQueries == 4, "The managed resident-query option must retain bit two.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, size) == 0,
    "The managed probe size field must remain at offset zero.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, cleared) == 4,
    "The managed probe cleared field must remain at offset four.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, residentPageQueries) == 8,
    "The managed resident-query count must remain at offset eight.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, residentPageHits) == 12,
    "The managed resident-hit count must remain at offset twelve.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, regionQueries) == 16,
    "The managed region-query count must remain at offset sixteen.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, accessFaulted) == 20,
    "The managed access-fault field must remain at offset twenty.");
static_assert(offsetof(irx::WritableCacheLineProbeResult, validatedEnd) == 24,
    "The managed validated-end field must remain at offset twenty-four.");

int main()
{
    auto* page = static_cast<volatile LONG64*>(VirtualAlloc(nullptr, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE));
    if (!page)
    {
        return 1;
    }
    *page = static_cast<LONG64>(0x1122334455667788ULL);
    const auto address = reinterpret_cast<std::uintptr_t>(page);
    const bool low = irx::TryPublishResidentScalar(address, 0xaabbccddULL, 4);
    const bool lowCorrect = static_cast<std::uint64_t>(*page) == (low ? 0x11223344aabbccddULL : 0x1122334455667788ULL);
    const auto beforeWide = *page;
    const bool wide = irx::TryPublishResidentScalar(address, 42, 8);
    const bool wideCorrect = *page == (wide ? 42 : beforeWide);
    const auto beforeProtection = *page;
    const bool invalid = irx::TryPublishResidentScalar(address + 1, 0, 4) ||
        irx::TryPublishResidentScalar(address, 0, 2) || irx::TryPublishResidentScalar(0, 0, 8);
    DWORD previous = 0;
    const bool protectedPage = VirtualProtect(const_cast<LONG64*>(page), 4096, PAGE_READONLY, &previous) != FALSE;
    const bool readOnlyRejected = protectedPage && !irx::TryPublishResidentScalar(address, 0, 8) && *page == beforeProtection;
    VirtualFree(const_cast<LONG64*>(page), 0, MEM_RELEASE);
    // The OS can legitimately reject a resident-page query. The production path falls back.
    if (!lowCorrect || !wideCorrect || invalid || !readOnlyRejected)
    {
        return 2;
    }
    std::printf("Guarded publication width and protection contracts passed (resident writes: %u/%u).\n", low ? 1U : 0U, wide ? 1U : 0U);
    std::puts("Cache-line geometry, resident protection, and managed ABI compile-time contracts passed.");

    return 0;
}
