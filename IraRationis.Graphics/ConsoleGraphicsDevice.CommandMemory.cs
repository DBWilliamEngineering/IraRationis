// SPDX-License-Identifier: GPL-3.0-or-later
// ABI and comparison conventions adapted from DexNom/VIctory victory-gpu/src/list.rs.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int MemoryWaitTimeoutMilliseconds = 30000;
    private const int MaximumMemoryOperations = 65536;

    // Use kernel-checked access rather than dereferencing arbitrary guest addresses.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int ReadProcessMemory(nint process, void* address, void* buffer, nuint size,
        nuint* transferred);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int WriteProcessMemory(nint process, void* address, void* buffer, nuint size,
        nuint* transferred);

    private static bool TryReadMemory(ulong address, int bytes, out ulong value)
    {
        if (TryReadFenceAddress(address, bytes, out value))
        {
            return true;
        }

        ulong raw = 0;

        nuint transferred = 0;

        var ok = ReadProcessMemory(-1, (void*)(nuint)address, &raw, (nuint)bytes, &transferred);

        value = raw;

        return ok != 0 && transferred == (nuint)bytes;
    }

    private static void PublishMemory(ulong address, ulong value, int bytes)
    {
        if (TryPublishFenceAddress(address, value, bytes))
        {
            return;
        }

        nuint transferred = 0;

        if (WriteProcessMemory(-1, (void*)(nuint)address, &value, (nuint)bytes, &transferred) == 0 ||
            transferred != (nuint)bytes)
        {
            Environment.FailFast($"[graphics] Deferred memory write failed: address=0x{address:X}, bytes={bytes}.");
        }
    }

    private static void RecordMemoryWrite(nint self, ulong address, ulong value, int bytes, uint flags)
    {
        var state = StateFor(self);

        if (address == 0 || address % (ulong)bytes != 0 || state.MemoryBoundaries.Count >= MaximumMemoryOperations)
        {
            Environment.FailFast("[graphics] Invalid command memory write address or count.");
        }

        SealMemoryBoundary(self, state, new MemoryWrite(address, value, bytes, flags), null);
    }

    private static void RecordMemoryWait(nint self, ulong address, uint comparison, ulong reference, int bytes)
    {
        var state = StateFor(self);

        if (address == 0 || address % (ulong)bytes != 0 || comparison is < 1 or > 8 ||
            state.MemoryBoundaries.Count >= MaximumMemoryOperations)
        {
            Environment.FailFast("[graphics] Invalid command memory wait address, comparison or count.");
        }

        SealMemoryBoundary(self, state, null, new MemoryWait(address, comparison, reference, bytes));
    }

    // Close the preceding packet range before opening the next. The guest list remains open.
    // The guest allocator owns all host command memory until its normal fence/reset lifecycle.
    private static void SealMemoryBoundary(nint self, RecordedCommandState state, MemoryWrite? write, MemoryWait? wait,
        DeferredCopy? copy = null, R13DrawBoundary? draw = null, R14DrawBoundary? rectangle = null,
        R16DrawBoundary? textured = null, R17ComputeBoundary? compute = null)
    {
        using var r25Timing = BeginR25Timing(R25Stage.SealBoundary);

        var current = Host(self);

        if (current == 0) // Synthetic ABI probe only.
        {
            state.MemoryBoundaries.Add(new MemoryBoundary(0, write, wait, copy, draw, rectangle, textured, compute));

            return;
        }

        ReportR3BoundaryOnce();

        var hr = ((ID3D12GraphicsCommandList*)current)->Close();

        if (hr < 0)
        {
            var boundary = compute != null ? "compute" :
                textured.HasValue ? "textured-rectangle" :
                rectangle.HasValue ? "rectangle" :
                draw.HasValue ? "vertex-fetch" :
                write.HasValue ? "write" :
                wait.HasValue ? "wait" : "copy";

            ReportHostCloseFailure(self, hr, boundary);

            Environment.FailFast($"[graphics] IRX-GFX-CLOSE-R3: closing memory segment failed: 0x{hr:X8}.");
        }

        var device = (nint)_host;
        var allocator = Host(*(nint*)(self + ListAllocator));
        var id = HostListId;

        nint next = 0;

        hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12))
            (device, 0, *(uint*)(self + 0x10), allocator, 0, &id, &next);

        if (hr < 0 || next == 0)
        {
            Environment.FailFast($"[graphics] Opening memory segment failed: 0x{hr:X8}.");
        }

        state.MemoryBoundaries.Add(new MemoryBoundary(current, write, wait, copy, draw, rectangle, textured, compute));

        var enhanced = *(nint*)(self + ListEnhancedHost);

        if (enhanced != 0)
        {
            _ = HostRelease(enhanced);

            *(nint*)(self + ListEnhancedHost) = 0;
        }

        *(nint*)(self + ListHost) = next;

        state.LastHostCommand = null;

        state.HostCommandCount = 0;

        ReplayRasterState(self, state);
    }

    private static bool CompareMemory(ulong value, ulong reference, uint comparison, int bytes)
    {
        if (bytes == 4)
        {
            value = (uint)value;

            reference = (uint)reference;
        }

        return comparison switch
        {
            1 => false,
            2 => value < reference,
            3 => value == reference,
            4 => value <= reference,
            5 => value > reference,
            6 => value != reference,
            7 => value >= reference,
            8 => true,
            _ => false
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Write32(nint self, ulong address, uint value, uint flush, uint flags)
    {
        RecordMemoryWrite(self, address, value, 4, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Write64(nint self, ulong address, ulong value, uint flush, uint flags)
    {
        RecordMemoryWrite(self, address, value, 8, flags);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void WaitValue32(nint self, ulong address, uint comparison, ulong reference)
    {
        RecordMemoryWait(self, address, comparison, reference, 4);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void WaitValue64(nint self, ulong address, uint comparison, ulong reference)
    {
        RecordMemoryWait(self, address, comparison, reference, 8);
    }

    private static void RegisterCommandMemoryMethods(nint* table, bool newer)
    {
        var shift = newer ? 2 : 0;

        table[69 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, uint, uint, void>)&Write32;

        table[70 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, uint, void>)&Write64;

        table[71 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, ulong, void>)&WaitValue32;

        table[72 + shift] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, ulong, void>)&WaitValue64;

        // InitializeTables constructs both queue tables before building either list.
        RegisterPageMappingMethods(newer);
    }

    private static void ValidateCommandMemoryRegistration()
    {
        ValidatePageMappingRegistration();

        for (var slot = 69; slot <= 72; slot++)
        {
            var old = ((nint*)_listOld)[slot];

            if (old == MissingAddress(slot) || ((nint*)_listNew)[slot + 2] != old)
            {
                throw new InvalidOperationException($"Command memory ABI registration failed at slot {slot}.");
            }
        }
    }

    private static string? CommandMemoryStatus(string kind, int slot)
    {
        return (kind == "list-old" && slot is >= 69 and <= 72) ||
               (kind == "list-new" && slot is >= 71 and <= 74)
            ? "registered-segmented-sync"
            : null;
    }

    private static bool HasCommandMemory(nint list)
    {
        return CommandStates.TryGetValue(list, out var state) &&
               state.MemoryBoundaries.Count != 0;
    }

    private static void AwaitCommandMemory(MemoryWait wait)
    {
        using var r25Timing = BeginR25Timing(R25Stage.GuestWait);

        var started = Stopwatch.GetTimestamp();

        while (true)
        {
            if (!TryReadMemory(wait.Address, wait.Bytes, out var value))
            {
                Environment.FailFast($"[graphics] Unmapped command memory wait: 0x{wait.Address:X}.");
            }

            if (CompareMemory(value, wait.Reference, wait.Comparison, wait.Bytes))
            {
                RecordR24GuestWait(Stopwatch.GetTimestamp() - started);

                return;
            }

            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= MemoryWaitTimeoutMilliseconds)
            {
                Environment.FailFast(
                    $"[graphics] Command memory wait timed out: address=0x{wait.Address:X}, comparison={wait.Comparison}, reference=0x{wait.Reference:X}, current=0x{value:X}. Segmented asynchronous queue scheduling may be required.");
            }

            Thread.Sleep(1);
        }
    }

    private static void WaitForHostCompletion(nint queue)
    {
        using var completion = new HostCompletionFence();

        completion.Wait(queue);
    }

    private static void SubmitWithCommandMemory(nint queue, nint list, nint hostList)
    {
        using var r25Timing = BeginR25Timing(R25Stage.SegmentedSubmit);

        if (CommandStates.TryGetValue(list, out var state) && state.MemoryBoundaries.Count != 0)
        {
            using var completion = new HostCompletionFence();

            foreach (var boundary in state.MemoryBoundaries)
            {
                var segment = boundary.Host;

                ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &segment);

                completion.Wait(queue);

                if (boundary.Write is { } write)
                {
                    PublishCommandWrite(queue, write);
                }

                if (boundary.Copy is { } copy)
                {
                    ExecuteDeferredCopy(copy);
                }

                if (boundary.Wait is { } wait)
                {
                    AwaitCommandMemory(wait);
                }

                if (boundary.Draw is { } draw)
                {
                    ExecuteR13DrawBoundary(queue, draw);
                }

                if (boundary.Rectangle is { } rectangle)
                {
                    ExecuteR14DrawBoundary(queue, rectangle);
                }

                if (boundary.Compute is { } compute)
                {
                    try
                    {
                        ExecuteR17Compute(queue, compute);
                    }
                    catch (Exception exception)
                    {
                        Environment.FailFast($"[graphics] R17 compute submission failed: {exception.Message}",
                            exception);
                    }
                }

                if (boundary.Textured is not { } textured)
                {
                    continue;
                }

                // Anon scope mem perf
                {
                    try
                    {
                        ExecuteR16DrawBoundary(queue, textured);
                    }
                    catch (Exception exception)
                    {
                        Environment.FailFast($"[graphics] R16 submission failed: {exception.Message}", exception);
                    }
                }
            }
        }

        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &hostList);
        // Earlier segments are retained until reset; the final segment uses the guest fence lifecycle.
    }

    private static void PublishCommandWrite(nint queue, MemoryWrite write)
    {
        using var r25Timing = BeginR25Timing(R25Stage.PublishWrite);

        var value = write.Value;

        switch (write.Bytes)
        {
            case 8 when write.Flags == 1:
            {
                using var calibrationTiming = BeginR25Timing(R25Stage.ClockCalibration);

                ulong gpu = 0, cpu = 0;
                // ID3D12CommandQueue::GetClockCalibration is slot 17.
                var hr = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)Slot(queue, 17))(queue, &gpu, &cpu);

                if (hr < 0)
                {
                    Environment.FailFast($"[graphics] GPU clock calibration failed: 0x{hr:X8}.");
                }

                value = gpu;

                break;
            }
            case 8 when write.Flags == 2:
                value = (ulong)Stopwatch.GetTimestamp();

                break;
        }

        using (BeginR25Timing(R25Stage.MemoryPublication))
        {
            PublishMemory(write.Address, value, write.Bytes);
        }
    }

    private static void VerifyCommandMemoryAbi()
    {
        ValidateCommandMemoryRegistration();

        VerifyPageMappingPlan();

        VerifyR16TexturePlan();

        var list = (nint)NativeMemory.AllocZeroed(0x980);

        try
        {
            ((Header*)list)->Table = _listNew;

            LiveObjects[list] = _listNew;

            var table = (nint*)_listNew;
            var target = 0xAABBCCDDEEFF0011;
            var address = (ulong)&target;
            var wait = (delegate* unmanaged[Stdcall]<nint, ulong, uint, ulong, void>)table[73];

            wait(list, address, 3, 0xEEFF0011);

            var write = (delegate* unmanaged[Stdcall]<nint, ulong, uint, uint, uint, void>)table[71];

            write(list, address, 0x12345678, 0, 0);

            var wide = (delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, uint, void>)table[72];

            wide(list, address, 42, 0, 0);

            wait(list, address, 3, 42);

            var state = StateFor(list);

            if (target != 0xAABBCCDDEEFF0011 || state.MemoryBoundaries.Count != 4 
                                             || state.MemoryBoundaries[0].Wait == null 
                                             || state.MemoryBoundaries[1].Write?.Bytes != 4 
                                             || state.MemoryBoundaries[2].Write?.Value != 42 
                                             || state.MemoryBoundaries[3].Wait?.Reference != 42 
                                             || !CompareMemory(target, 0xEEFF0011, 3, 4) 
                                             || CompareMemory(target, 0xEEFF0011, 3, 8) 
                                             || !TryReadMemory(address, 8, out var read) || read != target)
            {
                throw new InvalidOperationException("Command memory recording ABI failed.");
            }

            PublishMemory(address, 0x12345678, 4);

            if (target != 0xAABBCCDD12345678)
            {
                throw new InvalidOperationException("Command memory write width failed.");
            }

            VerifyR13BoundaryRecording(list, state);

            ReleaseCommandState(list);

            if (HasCommandMemory(list))
            {
                throw new InvalidOperationException("Command memory reset cleanup failed.");
            }
        }
        finally
        {
            ReleaseCommandState(list);

            _ = LiveObjects.TryRemove(list, out _);

            NativeMemory.Free((void*)list);
        }

        Console.WriteLine("[graphics-probe] Command memory write/wait ABI passed (CPU synthetic checks only).");
    }

    private readonly record struct MemoryWrite(ulong Address, ulong Value, int Bytes, uint Flags);

    private readonly record struct MemoryWait(ulong Address, uint Comparison, ulong Reference, int Bytes);

    private readonly record struct MemoryBoundary(
        nint Host,
        MemoryWrite? Write,
        MemoryWait? Wait,
        DeferredCopy? Copy,
        R13DrawBoundary? Draw,
        R14DrawBoundary? Rectangle,
        R16DrawBoundary? Textured,
        R17ComputeBoundary? Compute);
}
