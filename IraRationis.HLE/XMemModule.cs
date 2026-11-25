// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx victory-hle/src/modules/xmem.rs.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics;

namespace IraRationis.HLE;

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
            new HleExport("XMemAllocDefault", (nuint)(delegate* unmanaged[Stdcall]<nuint, ulong, void*>)&AllocateDefault),
            new HleExport("XMemFreeDefault", (nuint)(delegate* unmanaged[Stdcall]<void*, ulong, void>)&FreeDefault),
            new HleExport("XMemVirtualAlloc", (nuint)(delegate* unmanaged[Stdcall]<nint, nuint, uint, ulong, uint, nint>)&AllocateVirtual),
            new HleExport("XMemVirtualQuery", (nuint)(delegate* unmanaged[Stdcall]<nint, nint, BasicInformation*, int>)&QueryVirtual),
            new HleExport("XMemIsToolsMemAvailable", (nuint)(delegate* unmanaged[Stdcall]<int>)&ToolsMemoryAvailable),
            new HleExport("XMemGetWorkingSetStatistics", (nuint)(delegate* unmanaged[Stdcall]<uint, WorkingSet*, int>)&GetWorkingSet),
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
        var requirements = new AddressRequirements
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
        var parameter = new ExtendedParameter { Type = 1, Pointer = (nint)(&requirements) };

        if (constrained)
        {
            kind |= Reserve;

            if ((kind & ReservePlaceholder) == 0)
            {
                kind |= WriteWatch;
            }
        }

        return VirtualAlloc2(GetCurrentProcess(), address, size, kind, protection, constrained ? &parameter : null, constrained ? 1U : 0U);
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

            return NativeMemory.AlignedAlloc(size == 0 ? (nuint)1 : size, alignment);
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
    private static int QueryVirtual(nint process, nint address, BasicInformation* output)
    {
        if (output == null || VirtualQueryEx(process, address, &output->Host, (nuint)sizeof(MemoryInformation)) == 0)
        {
            return 0;
        }

        output->Flags = output->Host.State == Commit ? 3UL : 0UL;
        output->TagCode = 0;
        output->Tag = 0;

        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetWorkingSet(uint which, WorkingSet* output)
    {
        _ = which;

        if (output == null)
        {
            return 0;
        }

        var counters = new ProcessMemoryCounters { Size = (uint)sizeof(ProcessMemoryCounters) };

        if (GetProcessMemoryInfo(GetCurrentProcess(), &counters, counters.Size) == 0)
        {
            return 0;
        }

        var (gpu, standard) = ConsoleConfiguration.GetTitleMemory(ConsoleConfiguration.Current);
        *output = new WorkingSet
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

        if (kind != 0 || (ulong)size > ulong.MaxValue - granularity + 1)
        {
            return 0;
        }

        var rounded = ((ulong)size + granularity - 1) & ~(granularity - 1);
        var standard = ConsoleConfiguration.GetTitleMemory(ConsoleConfiguration.Current).Standard;

        while (true)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AddressRequirements
    {
        public nuint Lowest;
        public nuint Highest;
        public nuint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedParameter
    {
        public ulong Type;
        public nint Pointer;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct MemoryInformation
    {
        [FieldOffset(32)] public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public MemoryInformation Host;
        public ulong Flags;
        public uint TagCode;
        public uint Tag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSet
    {
        public nuint TitleLimit;
        public nuint TitleUsed;
        public nuint ToolsLimit;
        public nuint ToolsUsed;
        public uint StackUsed;
        public uint PhysicalPages;
        public nuint GpuOptimalLimit;
        public nuint GpuOptimalUsed;
        public nuint ToolsGpuOptimalLimit;
        public nuint ToolsGpuOptimalUsed;
        public uint KernelPoolUsed;
        public uint KernelPoolLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint VirtualAlloc2(nint process, nint address, nuint size, uint kind, uint protection, ExtendedParameter* parameters, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int VirtualFree(nint address, nuint size, uint kind);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint VirtualQueryEx(nint process, nint address, MemoryInformation* output, nuint length);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetProcessMemoryInfo(nint process, ProcessMemoryCounters* counters, uint size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcAddress(nint module, byte* name);
}
