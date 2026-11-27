// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx victory-hle/src/modules/xmem.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Device;
using IraRationis.HLE.Memory.Models;
using IraRationis.HLE.Runtime;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Memory;

public static unsafe partial class XMemModule
{
    private const uint Commit = 0x1000;
    private const uint Reserve = 0x2000;
    private const uint Release = 0x8000;
    private const uint ReservePlaceholder = 0x40000;
    private const uint ReplacePlaceholder = 0x4000;
    private const uint Pages2M = 0x20000000;
    private const uint Pages64KBit = 0x400000;
    private const uint Pages64K = Pages2M | Pages64KBit;
    private const uint WriteWatch = 0x200000;
    private const ulong Address32Bit = 4;
    private const ulong Allocation32Bit = 1UL << 33;
    private static long _transferred;

    public static HleModule Create()
    {
        return new HleModule("xmem", ["xmem.dll"],
        [
            new HleExport("XMemAlloc", (nuint)(delegate* unmanaged[Stdcall]<nuint, ulong, void*>)&Allocate),
            new HleExport("XMemFree", (nuint)(delegate* unmanaged[Stdcall]<void*, ulong, void>)&Free),
            new HleExport("XMemAllocDefault",
                (nuint)(delegate* unmanaged[Stdcall]<nuint, ulong, void*>)&AllocateDefault),
            new HleExport("XMemFreeDefault", (nuint)(delegate* unmanaged[Stdcall]<void*, ulong, void>)&FreeDefault),
            new HleExport("XMemVirtualAlloc",
                (nuint)(delegate* unmanaged[Stdcall]<nint, nuint, uint, ulong, uint, nint>)&AllocateVirtual),
            new HleExport("XMemVirtualQuery",
                (nuint)(delegate* unmanaged[Stdcall]<nint, nint, XMemBasicInformation*, int>)&QueryVirtual),
            new HleExport("XMemIsToolsMemAvailable", (nuint)(delegate* unmanaged[Stdcall]<int>)&ToolsMemoryAvailable),
            new HleExport("XMemGetWorkingSetStatistics",
                (nuint)(delegate* unmanaged[Stdcall]<uint, XMemWorkingSet*, int>)&GetWorkingSet),
            new HleExport("XMemTransferMemory", (nuint)(delegate* unmanaged[Stdcall]<uint, nuint, int>)&TransferMemory)
        ]);
    }

    public static (ulong Lowest, ulong Highest) AddressRange(ulong flags)
    {
        if ((flags & Address32Bit) != 0)
        {
            return (0, uint.MaxValue);
        }

        var field = (int)((flags >> 12) & 7);
        var bits = field == 0 ? 40 : 47 - field;

        return (1UL << 32, (1UL << bits) - 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint AllocateVirtual(nint address, nuint size, uint kind, ulong flags, uint protection)
    {
        return AllocateVirtualCore(address, size, kind, flags, protection);
    }

    private static nint AllocateVirtualCore(nint address, nuint size, uint kind, ulong flags, uint protection)
    {
        if (size == 0)
        {
            return 0;
        }

        var (lowest, highest) = AddressRange(flags);

        var requirements = new XMemAddressRequirements
        {
            Lowest = (nuint)lowest,
            Highest = (nuint)highest,
            Alignment = (kind & Pages64K) == Pages2M ? 2U << 20 : 0
        };

        protection &= 0x1FF;

        if (protection == 0)
        {
            protection = 1;
        }

        var placeholder = (kind & Pages64K) == Pages64K && (kind & Commit) == 0;

        if (placeholder)
        {
            // Upstream treats a split placeholder reservation as unchanged.
            if ((kind & ReplacePlaceholder) != 0)
            {
                return address;
            }

            protection = 1;
        }

        kind &= ~Pages64K;

        if (placeholder)
        {
            kind |= Reserve | ReservePlaceholder;
        }

        var constrained = address == 0;
        var parameter = new XMemExtendedParameter { Type = 1, Pointer = (nint)(&requirements) };

        if (constrained)
        {
            kind |= Reserve;

            if ((kind & ReservePlaceholder) == 0)
            {
                kind |= WriteWatch;
            }
        }

        return VirtualAlloc2(GetCurrentProcess(), address, size, kind, protection, constrained ? &parameter : null,
            constrained ? 1U : 0U);
    }

    private static nint GetHook(int index)
    {
        if (RuntimeState.MainImage is not { } image)
        {
            return 0;
        }

        var name = "__XMemAllocationHooks__\0"u8;

        fixed (byte* pointer = name)
        {
            var hooks = GetProcAddress((nint)image, pointer);

            return hooks == 0 ? 0 : ((nint*)hooks)[index];
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* Allocate(nuint size, ulong attributes)
    {
        try
        {
            var hook = GetHook(0);

            if (hook != 0)
            {
                return ((delegate* unmanaged[Stdcall]<nuint, ulong, void*>)hook)(size, attributes);
            }

            return AllocateDefaultCore(size, attributes);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Free(void* address, ulong attributes)
    {
        var hook = GetHook(1);

        if (hook != 0)
        {
            ((delegate* unmanaged[Stdcall]<void*, ulong, void>)hook)(address, attributes);

            return;
        }

        FreeDefaultCore(address, attributes);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* AllocateDefault(nuint size, ulong attributes)
    {
        return AllocateDefaultCore(size, attributes);
    }

    public static void* AllocateDefaultCore(nuint size, ulong attributes)
    {
        try
        {
            if ((attributes & Allocation32Bit) != 0)
            {
                return (void*)AllocateVirtualCore(0, size, Reserve | Commit, 3 | Address32Bit, 4);
            }

            var alignment = (nuint)1 << Math.Max(4, (int)((attributes >> 24) & 31));

            return NativeMemory.AlignedAlloc(size == 0 ? 1 : size, alignment);
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void FreeDefault(void* address, ulong attributes)
    {
        FreeDefaultCore(address, attributes);
    }

    public static void FreeDefaultCore(void* address, ulong attributes)
    {
        if (address == null)
        {
            return;
        }

        if ((attributes & Allocation32Bit) != 0)
        {
            _ = VirtualFree((nint)address, 0, Release);

            return;
        }

        NativeMemory.AlignedFree(address);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ToolsMemoryAvailable()
    {
        // A retail console has no tools memory, matching upstream.
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryVirtual(nint process, nint address, XMemBasicInformation* output)
    {
        if (output == null
            || VirtualQueryEx(process, address, &output->Host, (nuint)sizeof(XMemMemoryInformation)) == 0)
        {
            return 0;
        }

        output->Flags = output->Host.State == Commit ? 3UL : 0UL;

        output->TagCode = 0;

        output->Tag = 0;

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetWorkingSet(uint which, XMemWorkingSet* output)
    {
        _ = which;

        if (output == null)
        {
            return 0;
        }

        var counters = new XMemProcessMemoryCounters { Size = (uint)sizeof(XMemProcessMemoryCounters) };

        if (GetProcessMemoryInfo(GetCurrentProcess(), &counters, counters.Size) == 0)
        {
            return 0;
        }

        var (gpu, standard) = ConsoleConfiguration.GetTitleMemory(ConsoleConfiguration.Current);

        *output = new XMemWorkingSet
        {
            TitleLimit = (nuint)(gpu + standard - (ulong)Volatile.Read(ref _transferred)),
            TitleUsed = counters.PagefileUsage,
            GpuOptimalLimit = (nuint)gpu
        };

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int TransferMemory(uint kind, nuint size)
    {
        if (size == 0)
        {
            return 1;
        }

        const ulong granularity = 2UL << 20;

        if (kind != 0 || size > ulong.MaxValue - granularity + 1)
        {
            return 0;
        }

        var rounded = (size + granularity - 1) & ~(granularity - 1);
        var standard = ConsoleConfiguration.GetTitleMemory(ConsoleConfiguration.Current).Standard;

        for (;;)
        {
            var old = Volatile.Read(ref _transferred);

            if ((ulong)old > standard || rounded > standard - (ulong)old)
            {
                return 0;
            }

            if (Interlocked.CompareExchange(ref _transferred, old + (long)rounded, old) == old)
            {
                return 1;
            }
        }
    }

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint VirtualAlloc2(nint process, nint address, nuint size, uint kind, uint protection,
        XMemExtendedParameter* parameters, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int VirtualFree(nint address, nuint size, uint kind);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint
        VirtualQueryEx(nint process, nint address, XMemMemoryInformation* output, nuint length);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetProcessMemoryInfo(nint process, XMemProcessMemoryCounters* counters, uint size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcAddress(nint module, byte* name);
}
