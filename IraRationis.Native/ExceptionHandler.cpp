// SPDX-License-Identifier: GPL-3.0-or-later
// Native exception boundary; instruction semantics adapted from the existing C# port.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <intrin.h>
#include <cstring>
#include "InstructionDecoder.h"

namespace
{
    struct ImageRange { ULONG_PTR start; ULONG_PTR end; };
    constexpr unsigned MaxRanges = 256;
    ImageRange ranges[MaxRanges]{};
    unsigned rangeCount = 0;
    PVOID registration = nullptr;
    volatile LONG active = 0;
    volatile LONG breakpointReports = 0;
    __declspec(thread) bool handling = false;

    ULONG_PTR RegionEnd(ULONG_PTR address, bool writable) noexcept
    {
        MEMORY_BASIC_INFORMATION info{};
        if (!VirtualQuery(reinterpret_cast<void*>(address), &info, sizeof(info)) || info.State != MEM_COMMIT || (info.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
        {
            return 0;
        }
        const auto protection = info.Protect & 0xff;
        const bool write = protection == PAGE_READWRITE || protection == PAGE_WRITECOPY || protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
        const bool read = write || protection == PAGE_READONLY || protection == PAGE_EXECUTE_READ;
        if ((writable && !write) || (!writable && !read))
        {
            return 0;
        }
        const auto start = reinterpret_cast<ULONG_PTR>(info.BaseAddress);
        if (info.RegionSize > UINTPTR_MAX - start)
        {
            return 0;
        }
        return start + info.RegionSize;
    }

    LONG Emulate(CONTEXT& ctx) noexcept
    {
        const auto rip = static_cast<ULONG_PTR>(ctx.Rip);
        bool allowed = false;
        for (unsigned i = 0; i < rangeCount; ++i)
        {
            if (rip >= ranges[i].start && rip < ranges[i].end)
            {
                allowed = true;
                break;
            }
        }
        if (!allowed)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        const auto end = RegionEnd(rip, false);
        if (end <= rip)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        const auto size = end-rip < 16 ? end-rip : 16;
        const auto instruction = irx::Decode(reinterpret_cast<const std::uint8_t*>(rip), size);
        if (instruction.kind == irx::Kind::Unknown || instruction.kind == irx::Kind::Ud2)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        DWORD64* registers[] = {&ctx.Rax,&ctx.Rcx,&ctx.Rdx,&ctx.Rbx,&ctx.Rsp,&ctx.Rbp,&ctx.Rsi,&ctx.Rdi,&ctx.R8,&ctx.R9,&ctx.R10,&ctx.R11,&ctx.R12,&ctx.R13,&ctx.R14,&ctx.R15};
        auto* xmm = ctx.FltSave.XmmRegisters;
        switch (instruction.kind)
        {
            case irx::Kind::Clwb:
            case irx::Kind::Monitorx:
                break;
            case irx::Kind::Mwaitx:
                _mm_pause();
                break;
            case irx::Kind::Clzero:
            case irx::Kind::ClzeroLoop:
            {
                // Execute one cache line; the original ADD/CMP/JB preserve exact flag and loop semantics.
                const auto address = instruction.address32 ? ctx.Rax & UINT32_MAX : ctx.Rax;
                const auto line = static_cast<ULONG_PTR>(address) & ~static_cast<ULONG_PTR>(63);
                if (line > UINTPTR_MAX-64 || RegionEnd(line, true) < line+64)
                {
                    return EXCEPTION_CONTINUE_SEARCH;
                }
                std::memset(reinterpret_cast<void*>(line), 0, 64);
                ctx.Rip += instruction.kind == irx::Kind::ClzeroLoop ? instruction.length-9 : instruction.length;
                return EXCEPTION_CONTINUE_EXECUTION;
            }
            case irx::Kind::Rdpru:
            {
                LARGE_INTEGER counter{};
                QueryPerformanceCounter(&counter);
                const auto value = static_cast<DWORD>(ctx.Rcx) <= 1 ? static_cast<DWORD64>(counter.QuadPart) : 0;
                ctx.Rax = value & UINT32_MAX;
                ctx.Rdx = value >> 32;
                break;
            }
            case irx::Kind::Rdpid:
                *registers[instruction.destination] = GetCurrentProcessorNumber();
                break;
            case irx::Kind::Extrq:
            {
                auto length = instruction.fieldLength;
                auto index = instruction.fieldIndex;
                if (instruction.source >= 0)
                {
                    const auto fields = xmm[instruction.source].Low;
                    length = static_cast<unsigned>(fields & 0xff);
                    index = static_cast<unsigned>((fields >> 8) & 0xff);
                }
                auto& dst = xmm[instruction.destination];
                dst.Low = irx::Extract(dst.Low,length,index);
                dst.High = 0;
                break;
            }
            case irx::Kind::Insertq:
            {
                auto& dst = xmm[instruction.destination];
                const auto src = xmm[instruction.source];
                const auto fields = static_cast<ULONGLONG>(src.High);
                const auto length = instruction.registerField ? static_cast<unsigned>(fields & 0xff) : instruction.fieldLength;
                const auto index = instruction.registerField ? static_cast<unsigned>((fields >> 8) & 0xff) : instruction.fieldIndex;
                dst.Low = irx::Insert(dst.Low,src.Low,length,index);
                dst.High = 0;
                break;
            }
            default:
                return EXCEPTION_CONTINUE_SEARCH;
        }
        ctx.Rip += instruction.length;
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    // Diagnostic only: fixed stack buffer, no managed callback or heap allocation.
    // Do not modify the exception context or consume breakpoint exceptions.
    void AppendText(char*& cursor, const char* value) noexcept
    {
        while (*value)
        {
            *cursor++ = *value++;
        }
    }

    void AppendHex(char*& cursor, ULONG64 value) noexcept
    {
        constexpr char digits[] = "0123456789ABCDEF";
        AppendText(cursor, "0x");
        for (int shift = 60; shift >= 0; shift -= 4)
        {
            *cursor++ = digits[(value >> shift) & 0xf];
        }
    }

    void ReportBreakpoint(const EXCEPTION_POINTERS& exception) noexcept
    {
        if (InterlockedIncrement(&breakpointReports) > 16)
        {
            return;
        }
        char message[256]{};
        char* cursor = message;
        const auto address = reinterpret_cast<ULONG_PTR>(exception.ExceptionRecord->ExceptionAddress);
        AppendText(cursor, "[veh] First-chance breakpoint address=");
        AppendHex(cursor, address);
        AppendText(cursor, " rip=");
        AppendHex(cursor, exception.ContextRecord->Rip);
        AppendText(cursor, " thread=");
        AppendHex(cursor, GetCurrentThreadId());
        for (unsigned i = 0; i < rangeCount; ++i)
        {
            if (address >= ranges[i].start && address < ranges[i].end)
            {
                AppendText(cursor, " image-base=");
                AppendHex(cursor, ranges[i].start);
                AppendText(cursor, " rva=");
                AppendHex(cursor, address - ranges[i].start);
                break;
            }
        }
        AppendText(cursor, " (not handled)\r\n");
        const auto standardError = GetStdHandle(STD_ERROR_HANDLE);
        if (standardError && standardError != INVALID_HANDLE_VALUE)
        {
            DWORD written = 0;
            WriteFile(standardError, message, static_cast<DWORD>(cursor - message), &written, nullptr);
        }
    }

    LONG CALLBACK Handle(EXCEPTION_POINTERS* exception) noexcept
    {
        if (!exception || !exception->ExceptionRecord || !exception->ContextRecord || InterlockedCompareExchange(&active, 0, 0) == 0)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        const auto code = exception->ExceptionRecord->ExceptionCode;
        if (code != EXCEPTION_ILLEGAL_INSTRUCTION && code != EXCEPTION_BREAKPOINT)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        if (handling)
        {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        handling = true;
        LONG result = EXCEPTION_CONTINUE_SEARCH;
        __try
        {
            if (code == EXCEPTION_BREAKPOINT)
            {
                ReportBreakpoint(*exception);
            }
            else
            {
                result = Emulate(*exception->ContextRecord);
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            // A mapping can change between VirtualQuery and the actual access.
            result = EXCEPTION_CONTINUE_SEARCH;
        }
        handling = false;
        return result;
    }
}

extern "C" __declspec(dllexport) DWORD WINAPI IrxInstallHandler(const ImageRange* input, unsigned count) noexcept
{
    // Called once, before title threads start. Ranges immutable until process exit.
    if (!input || count == 0 || count > MaxRanges || registration)
    {
        return ERROR_INVALID_PARAMETER;
    }
    for (unsigned i = 0; i < count; ++i)
    {
        if (input[i].start >= input[i].end)
        {
            return ERROR_INVALID_PARAMETER;
        }
        ranges[i] = input[i];
    }
    rangeCount = count;
    registration = AddVectoredExceptionHandler(1, Handle);
    if (!registration)
    {
        const auto error = GetLastError();
        return error ? error : ERROR_GEN_FAILURE;
    }
    InterlockedExchange(&active, 1);
    return ERROR_SUCCESS;
}

extern "C" __declspec(dllexport) void WINAPI IrxDisableHandler() noexcept
{
    // Keep DLL and registration resident. A handler may already be in flight.
    InterlockedExchange(&active, 0);
}
