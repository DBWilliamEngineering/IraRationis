// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Memory;
using IraRationis.HLE.Runtime;

namespace IraRationis.Runtime.Tests.Memory;

internal static unsafe partial class PhysicalPageTests
{
    private const int PageSize = 65536;
    private const int ReservationPages = 8;

    public static void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var registry = new HleRegistry([PhysicalPageModule.Create()]);

        nuint Export(string name)
        {
            return registry.Lookup("xmem.dll", name)
                ?? throw new InvalidOperationException($"Missing physical page export {name}.");
        }

        var allocate = (delegate* unmanaged[Stdcall]<ulong, nuint*, nuint*, int>)Export("XMemAllocatePhysicalPages");
        var free = (delegate* unmanaged[Stdcall]<nuint, nuint*, int>)Export("XMemFreePhysicalPages");
        var map = (delegate* unmanaged[Stdcall]<nint, nuint, nuint*, nint>)Export("XMemMapPhysicalPages");
        var scatter = (delegate* unmanaged[Stdcall]<nint*, nuint, nuint*, int>)Export("XMemMapPhysicalPagesScatter");
        var reservation = VirtualAlloc2(-1, 0, ReservationPages * PageSize, 0x42000, 1, 0, 0);

        Require(reservation != 0, "Reserve physical mapping placeholders");

        var pages = stackalloc nuint[4];
        var allocated = false;
        var completed = false;
        nuint count = 4;

        try
        {
            Require(allocate(0, &count, pages) != 0 && count == 4, "Allocate physical pages");
            allocated = true;

            Require(map(reservation, count, pages) == reservation, "Map contiguous physical pages");

            for (var page = 0; page < 4; page++)
            {
                *(int*)(reservation + page * PageSize) = (page + 1) * 10;
            }

            var address = (ulong)(nuint)reservation;

            Require(PhysicalPageModule.MapAlias(address + 4 * PageSize, address + PageSize, 2 * PageSize),
                "Alias interior portion of contiguous view");
            Require(*(int*)(reservation + 4 * PageSize) == 20 && *(int*)(reservation + 5 * PageSize) == 30,
                "Alias preserves physical offsets");

            *(int*)(reservation + 4 * PageSize) = 21;

            Require(*(int*)(reservation + PageSize) == 21, "Alias shares backing storage");

            var addresses = stackalloc nint[2] { reservation + PageSize, reservation + 3 * PageSize };
            var selectedPages = stackalloc nuint[2] { pages[1], pages[3] };

            Require(scatter(addresses, 2, selectedPages) != 0, "Scatter replacement splits tracked views");
            Require(free(count, pages) != 0, "Free page identifiers while views remain mapped");
            allocated = false;

            Require(PhysicalPageModule.MapAlias(address + PageSize, address, 3 * PageSize),
                "Overlapping alias captures source before destination changes");
            Require(*(int*)reservation == 10 && *(int*)(reservation + PageSize) == 10
                && *(int*)(reservation + 2 * PageSize) == 21 && *(int*)(reservation + 3 * PageSize) == 30,
                "Overlapping alias retains original source identities after physical IDs are freed");

            Require(map(reservation + 2 * PageSize, 1, null) == reservation + 2 * PageSize,
                "Unmap interior page");
            Require(*(int*)(reservation + PageSize) == 10 && *(int*)(reservation + 3 * PageSize) == 30,
                "Partial unmap preserves surviving views");
            Require(!PhysicalPageModule.MapAlias(address + 4 * PageSize, address + PageSize, 3 * PageSize),
                "Alias source containing an unmapped gap is rejected");
            Require(*(int*)(reservation + 4 * PageSize) == 21 && *(int*)(reservation + 5 * PageSize) == 30,
                "Invalid alias source leaves destination mappings unchanged");
            completed = true;
        }
        finally
        {
            var pagesFreed = !allocated || free(count, pages) != 0;
            var viewsUnmapped = map(reservation, ReservationPages, null) == reservation;
            var placeholdersCoalesced = VirtualFree(reservation, ReservationPages * PageSize, 0x8001) != 0;
            var reservationReleased = VirtualFree(reservation, 0, 0x8000) != 0;
            var cleanedUp = pagesFreed && viewsUnmapped && placeholdersCoalesced && reservationReleased;

            if (completed)
            {
                Require(cleanedUp, "Release physical mapping test resources");
            }
            else if (!cleanedUp)
            {
                Console.Error.WriteLine("[physical-page-tests] Cleanup incomplete after the original failure.");
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{message}; last error={Marshal.GetLastPInvokeError()}.");
        }
    }

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint VirtualAlloc2(nint process, nint address, nuint size, uint allocationType,
        uint protection, nint parameters, uint parameterCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int VirtualFree(nint address, nuint size, uint freeType);
}
