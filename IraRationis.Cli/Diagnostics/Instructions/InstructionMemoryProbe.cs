// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using IraRationis.Cli.Diagnostics.Instructions.Models;
using IraRationis.Loader.Instructions.Models;

namespace IraRationis.Cli.Diagnostics.Instructions;

public static unsafe class InstructionMemoryProbe
{
    private const int CacheLineBytes = 64;
    private const byte Canary = 0xA5;

    public static void Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The instruction memory probe requires Windows x64.");
        }

        var pageSize = Environment.SystemPageSize;

        Require(pageSize >= CacheLineBytes * 3 && pageSize % CacheLineBytes == 0,
            "The host page size must contain complete cache lines.");
        Require(sizeof(WritableCacheLineProbeResult) == 32 && sizeof(InstructionProbeMemoryInformation) == 48,
            "The instruction memory probe ABI does not match Windows x64.");

        var nativeProbe = new NativeInstructionMemoryProbe();

        foreach (var residentPageQueries in new[] { false, true })
        {
            RunDemandZero(output, nativeProbe, pageSize, residentPageQueries);
            RunPrivateMemory(output, nativeProbe, pageSize, residentPageQueries);
            RunPageBoundary(output, nativeProbe, pageSize, residentPageQueries);
            RunUncommittedMemory(output, nativeProbe, pageSize, residentPageQueries);
            RunMappedMemory(output, nativeProbe, pageSize, residentPageQueries);
        }

        output.WriteLine("Instruction memory validation contracts passed (authored allocations only).");
    }

    private static void RunDemandZero(TextWriter output, NativeInstructionMemoryProbe nativeProbe, int pageSize,
        bool residentPageQueries)
    {
        using var allocation = new InstructionProbeAllocation((nuint)pageSize);
        var result = Probe(output, nativeProbe, "demand-zero", allocation.Address, residentPageQueries);

        RequireCleared(result, allocation.Address);
        RequirePattern(Bytes(allocation.Address, pageSize), 0,
            "Demand-zero allocation changed outside its zero state.");
    }

    private static void RunPrivateMemory(TextWriter output, NativeInstructionMemoryProbe nativeProbe, int pageSize,
        bool residentPageQueries)
    {
        using var allocation = new InstructionProbeAllocation((nuint)pageSize);
        var page = Bytes(allocation.Address, pageSize);
        var target = allocation.Address + CacheLineBytes;

        if (residentPageQueries
            && InstructionProbeNativeMethods.VirtualLock((nint)allocation.Address, (nuint)pageSize) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Cannot pin the positive resident-page probe fixture.");
        }

        try
        {
            page.Fill(Canary);

            var residentResult = Probe(output, nativeProbe, "resident-private", target, residentPageQueries);

            RequireCleared(residentResult, target);
            RequireClearedLine(page, CacheLineBytes);

            if (residentPageQueries)
            {
                Require(residentResult.ResidentPageQueries == 1 && residentResult.ResidentPageHits == 1
                                                                && residentResult.RegionQueries == 0
                                                                && residentResult.ValidatedEnd
                                                                == target + CacheLineBytes,
                    "A pinned RW page did not use the single-line working-set validation path.");
            }
            else
            {
                Require(residentResult.ResidentPageQueries == 0 && residentResult.ResidentPageHits == 0
                                                                && residentResult.RegionQueries == 1,
                    "Baseline validation must use one region query.");
            }
        }
        finally
        {
            if (residentPageQueries
                && InstructionProbeNativeMethods.VirtualUnlock((nint)allocation.Address, (nuint)pageSize) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "Cannot unpin the resident-page probe fixture.");
            }
        }

        page.Fill(Canary);

        RequireRejected(Probe(output, nativeProbe, "unaligned", target + 1, residentPageQueries), true);
        RequireRejected(Probe(output, nativeProbe, "address-overflow", nuint.MaxValue - 63, residentPageQueries), true);
        RequirePattern(page, Canary, "Invalid addresses changed the allocation.");

        RunProtectionChanges(output, nativeProbe, allocation.Address, pageSize, residentPageQueries);
    }

    private static void RunProtectionChanges(TextWriter output, NativeInstructionMemoryProbe nativeProbe,
        nuint address, int pageSize, bool residentPageQueries)
    {
        foreach (var protection in new[]
                 {
                     InstructionProbeNativeMethods.ReadOnly,
                     InstructionProbeNativeMethods.NoAccess,
                     InstructionProbeNativeMethods.ReadWrite | InstructionProbeNativeMethods.Guard
                 })
        {
            Bytes(address, pageSize).Fill(Canary);
            Protect(address, (nuint)pageSize, protection);

            var caseName = protection switch
            {
                InstructionProbeNativeMethods.ReadOnly => "resident-then-readonly",
                InstructionProbeNativeMethods.NoAccess => "resident-then-noaccess",
                _ => "resident-then-guard"
            };

            RequireRejected(Probe(output, nativeProbe, caseName, address + CacheLineBytes, residentPageQueries));
            Require(Query(address).Protection == protection,
                "Validation changed the page protection or consumed its guard.");

            Protect(address, (nuint)pageSize, InstructionProbeNativeMethods.ReadWrite);
            RequirePattern(Bytes(address, pageSize), Canary, "Rejected validation changed protected memory.");
        }
    }

    private static void RunPageBoundary(TextWriter output, NativeInstructionMemoryProbe nativeProbe, int pageSize,
        bool residentPageQueries)
    {
        using var allocation = new InstructionProbeAllocation((nuint)pageSize * 2);
        var firstPage = Bytes(allocation.Address, pageSize);
        var secondAddress = allocation.Address + (nuint)pageSize;

        firstPage.Fill(Canary);
        Bytes(secondAddress, pageSize).Fill(Canary);
        Protect(secondAddress, (nuint)pageSize, InstructionProbeNativeMethods.NoAccess);

        var lastLine = secondAddress - CacheLineBytes;

        RequireCleared(Probe(output, nativeProbe, "last-line-before-noaccess", lastLine, residentPageQueries),
            lastLine);
        RequireClearedLine(firstPage, pageSize - CacheLineBytes);
        Require(Query(secondAddress).Protection == InstructionProbeNativeMethods.NoAccess,
            "A cache-line clear changed the neighboring inaccessible page protection.");

        Protect(secondAddress, (nuint)pageSize, InstructionProbeNativeMethods.ReadWrite);
        RequirePattern(Bytes(secondAddress, pageSize), Canary, "A cache-line clear crossed a page boundary.");
    }

    private static void RunUncommittedMemory(TextWriter output, NativeInstructionMemoryProbe nativeProbe, int pageSize,
        bool residentPageQueries)
    {
        using var allocation = new InstructionProbeAllocation((nuint)pageSize, false);
        var address = allocation.Address;

        RequireRejected(Probe(output, nativeProbe, "reserved", address, residentPageQueries));
        Commit(address, (nuint)pageSize);
        Bytes(address, pageSize).Fill(Canary);

        if (InstructionProbeNativeMethods.VirtualFree((nint)address, (nuint)pageSize,
                InstructionProbeNativeMethods.Decommit) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot decommit instruction probe memory.");
        }

        RequireRejected(Probe(output, nativeProbe, "resident-then-decommitted", address, residentPageQueries));
        Commit(address, (nuint)pageSize);
        Bytes(address, pageSize).Fill(Canary);

        RequireCleared(Probe(output, nativeProbe, "recommitted", address + CacheLineBytes, residentPageQueries),
            address + CacheLineBytes);
        RequireClearedLine(Bytes(address, pageSize), CacheLineBytes);
    }

    private static void RunMappedMemory(TextWriter output, NativeInstructionMemoryProbe nativeProbe, int pageSize,
        bool residentPageQueries)
    {
        using var mapping = new InstructionProbeMapping((nuint)pageSize);
        var firstAddress = mapping.Map(InstructionProbeNativeMethods.MapWrite);
        var aliasAddress = mapping.Map(InstructionProbeNativeMethods.MapWrite);
        var copyAddress = mapping.Map(InstructionProbeNativeMethods.MapCopy);
        var firstView = Bytes(firstAddress, pageSize);
        var aliasView = Bytes(aliasAddress, pageSize);
        var copyView = Bytes(copyAddress, pageSize);

        firstView.Fill(Canary);
        RequirePattern(aliasView, Canary, "Mapped aliases do not share backing memory.");

        RequireCleared(Probe(output, nativeProbe, "mapped-alias", firstAddress + CacheLineBytes, residentPageQueries),
            firstAddress + CacheLineBytes);
        RequireClearedLine(firstView, CacheLineBytes);
        RequireClearedLine(aliasView, CacheLineBytes);

        firstView.Fill(Canary);
        Protect(firstAddress, (nuint)pageSize, InstructionProbeNativeMethods.ReadOnly);

        RequireRejected(Probe(output, nativeProbe, "mapped-readonly", firstAddress + CacheLineBytes,
            residentPageQueries));
        RequirePattern(aliasView, Canary, "Rejected mapped validation changed the shared backing memory.");

        Protect(firstAddress, (nuint)pageSize, InstructionProbeNativeMethods.ReadWrite);
        RequirePattern(copyView, Canary, "The fresh copy-on-write view differs from its source.");

        RequireCleared(Probe(output, nativeProbe, "copy-on-write", copyAddress + CacheLineBytes, residentPageQueries),
            copyAddress + CacheLineBytes);
        RequireClearedLine(copyView, CacheLineBytes);
        RequirePattern(firstView, Canary, "Copy-on-write modified its source view.");
        RequirePattern(aliasView, Canary, "Copy-on-write modified a shared alias.");
    }

    private static WritableCacheLineProbeResult Probe(TextWriter output, NativeInstructionMemoryProbe nativeProbe,
        string caseName, nuint address, bool residentPageQueries)
    {
        var result = nativeProbe.Clear(address, residentPageQueries);

        output.WriteLine(
            $"[instruction-memory] case={caseName}; residentPageQueries={residentPageQueries}; cleared={result.Cleared}; pageQueries={result.ResidentPageQueries}; pageHits={result.ResidentPageHits}; regionQueries={result.RegionQueries}; accessFaulted={result.AccessFaulted}; validatedEnd=0x{result.ValidatedEnd:X}.");

        Require(result.Size == 32 && result.Cleared <= 1 && result.AccessFaulted <= 1,
            "The native instruction probe returned an invalid result.");
        Require(residentPageQueries || result.ResidentPageQueries == 0,
            "Baseline validation unexpectedly queried the working set.");

        return result;
    }

    private static void RequireCleared(WritableCacheLineProbeResult result, nuint address)
    {
        Require(result.Cleared == 1 && result.AccessFaulted == 0
                                    && result.ValidatedEnd >= address + CacheLineBytes,
            "A writable cache line was not validated and cleared.");
    }

    private static void RequireRejected(WritableCacheLineProbeResult result, bool noQueries = false)
    {
        Require(result.Cleared == 0 && result.AccessFaulted == 0,
            "Invalid memory reached the store instead of being rejected by validation.");

        if (noQueries)
        {
            Require(result.ResidentPageQueries == 0 && result.ResidentPageHits == 0 && result.RegionQueries == 0,
                "Invalid cache-line arithmetic reached memory validation.");
        }
    }

    private static void RequireClearedLine(ReadOnlySpan<byte> bytes, int offset)
    {
        RequirePattern(bytes[..offset], Canary, "Bytes preceding the cache line were modified.");
        RequirePattern(bytes.Slice(offset, CacheLineBytes), 0, "The complete cache line was not cleared.");
        RequirePattern(bytes[(offset + CacheLineBytes)..], Canary, "Bytes following the cache line were modified.");
    }

    private static void RequirePattern(ReadOnlySpan<byte> bytes, byte expected, string message)
    {
        // O(n) over authored probe buffers; never runs in the exception handler.
        foreach (var value in bytes)
        {
            Require(value == expected, message);
        }
    }

    private static Span<byte> Bytes(nuint address, int length)
    {
        return new Span<byte>((void*)address, length);
    }

    private static void Protect(nuint address, nuint size, uint protection)
    {
        var previousProtection = 0U;

        if (InstructionProbeNativeMethods.VirtualProtect((nint)address, size, protection, &previousProtection) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Cannot change instruction probe memory protection.");
        }
    }

    private static void Commit(nuint address, nuint size)
    {
        if (InstructionProbeNativeMethods.VirtualAlloc((nint)address, size, InstructionProbeNativeMethods.Commit,
                InstructionProbeNativeMethods.ReadWrite) != (nint)address)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot commit instruction probe memory.");
        }
    }

    private static InstructionProbeMemoryInformation Query(nuint address)
    {
        var information = new InstructionProbeMemoryInformation();

        if (InstructionProbeNativeMethods.VirtualQuery((nint)address, &information,
                (nuint)sizeof(InstructionProbeMemoryInformation)) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot inspect instruction probe memory.");
        }

        return information;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
