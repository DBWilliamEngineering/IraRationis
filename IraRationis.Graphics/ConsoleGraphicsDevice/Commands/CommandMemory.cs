// SPDX-License-Identifier: GPL-3.0-or-later
// ABI and comparison conventions adapted from DexNom/VIctory victory-gpu/src/list.rs.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using IraRationis.Graphics.Commands;
using IraRationis.Graphics.Commands.Models;
using IraRationis.Graphics.Device.Models;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Synchronization;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    internal const int MemoryWaitTimeoutMilliseconds = 30000;
    private const int MaximumMemoryOperations = 65536;
    private static readonly bool EnableGuardedPublication = Environment.GetEnvironmentVariable("IRX_GUARDED_MEMORY_PUBLICATION") == "1";
    private static readonly bool SkipEmptyDrawSegments = Environment.GetEnvironmentVariable("IRX_SKIP_EMPTY_DRAW_SEGMENTS") == "1";
    private static int _guardedPublicationUnavailable;
    private static long _guardedPublicationHits;
    private static long _guardedPublicationMisses;
    private static long _skippedEmptyDrawSegments;

    [DllImport("irx-native", EntryPoint = "IrxTryPublishResidentScalar", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
    private static extern int TryPublishResidentScalar(nuint address, ulong value, uint bytes);

    private static bool TryGuardedPublication(ulong address, ulong value, int bytes)
    {
        if (!EnableGuardedPublication || Volatile.Read(ref _guardedPublicationUnavailable) != 0)
        {
            return false;
        }
        try
        {
            var accepted = TryPublishResidentScalar((nuint)address, value, (uint)bytes) != 0;
            var count = accepted ? Interlocked.Increment(ref _guardedPublicationHits) : Interlocked.Increment(ref _guardedPublicationMisses);
            if (count <= 2 || (count & (count - 1)) == 0)
            {
                Console.Error.WriteLine($"[graphics] IRX-R75-PUBLICATION: residentHits={Interlocked.Read(ref _guardedPublicationHits)}; kernelFallbacks={Interlocked.Read(ref _guardedPublicationMisses)}.");
            }
            return accepted;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            if (Interlocked.Exchange(ref _guardedPublicationUnavailable, 1) == 0)
            {
                Console.Error.WriteLine("[graphics] IRX-R75-PUBLICATION: native export unavailable; retaining kernel writes.");
            }
            return false;
        }
    }

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

        var readSucceeded = ReadProcessMemory(-1, (void*)(nuint)address, &raw, (nuint)bytes, &transferred);

        value = raw;

        return readSucceeded != 0 && transferred == (nuint)bytes;
    }

    private static void PublishMemory(ulong address, ulong value, int bytes)
    {
        using (BeginTiming(GraphicsStage.FencePublication))
        {
            if (TryPublishFenceAddress(address, value, bytes))
            {
                return;
            }
        }

        using (BeginTiming(GraphicsStage.GuardedPublication))
        {
            if (TryGuardedPublication(address, value, bytes))
            {
                return;
            }
        }

        using var kernelTiming = BeginTiming(GraphicsStage.KernelPublication);
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
        DeferredCopy? copy = null, VertexFetchDrawBoundary? draw = null, RectangleDrawBoundary? rectangle = null,
        TexturedDrawBoundary? textured = null, ColorLutComputeBoundary? compute = null,
        PlanarColorDrawBoundary? planarColor = null, IndexedUiBoundary? indexedUi = null)
    {
        using var stageTiming = BeginTiming(GraphicsStage.SealBoundary);

        var current = Host(self);
        var boundary = new MemoryBoundary(current, write, wait, copy, draw, rectangle, textured, compute, planarColor, indexedUi);

        if (current == 0) // Synthetic ABI probe only.
        {
            state.MemoryBoundaries.Add(boundary);

            return;
        }

        if (SkipEmptyDrawSegments && !state.HasHostGpuWork && boundary.IsDrawPreparation)
        {
            // Keep the open list with its raster state. Preserve the CPU action and queue fence.
            state.MemoryBoundaries.Add(boundary with { Host = 0 });
            Interlocked.Increment(ref _skippedEmptyDrawSegments);

            return;
        }

        ReportR3BoundaryOnce();

        var resultCode = ((ID3D12GraphicsCommandList*)current)->Close();

        if (resultCode < 0)
        {
            var boundaryName = indexedUi.HasValue ? "indexed-ui" : planarColor.HasValue ? "planar-color" :
                compute != null ? "compute" :
                textured.HasValue ? "textured-rectangle" :
                rectangle.HasValue ? "rectangle" :
                draw.HasValue ? "vertex-fetch" :
                write.HasValue ? "write" :
                wait.HasValue ? "wait" : "copy";

            ReportHostCloseFailure(self, resultCode, boundaryName);

            Environment.FailFast($"[graphics] IRX-GFX-CLOSE-R3: closing memory segment failed: 0x{resultCode:X8}.");
        }

        var device = (nint)_host;
        var allocator = Host(*(nint*)(self + ListAllocator));
        var id = HostListId;

        nint next = 0;

        resultCode = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, Guid*, nint*, int>)Slot(device, 12))
            (device, 0, *(uint*)(self + 0x10), allocator, 0, &id, &next);

        if (resultCode < 0 || next == 0)
        {
            Environment.FailFast($"[graphics] Opening memory segment failed: 0x{resultCode:X8}.");
        }

        state.MemoryBoundaries.Add(boundary);

        var enhanced = *(nint*)(self + ListEnhancedHost);

        if (enhanced != 0)
        {
            _ = HostRelease(enhanced);

            *(nint*)(self + ListEnhancedHost) = 0;
        }

        *(nint*)(self + ListHost) = next;
        state.HasHostGpuWork = false;

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
        using var stageTiming = BeginTiming(GraphicsStage.GuestWait);

        var started = Stopwatch.GetTimestamp();

        for(;;)
        {
            if (!TryReadMemory(wait.Address, wait.Bytes, out var value))
            {
                Environment.FailFast($"[graphics] Unmapped command memory wait: 0x{wait.Address:X}.");
            }

            if (CompareMemory(value, wait.Reference, wait.Comparison, wait.Bytes))
            {
                RecordGuestWait(Stopwatch.GetTimestamp() - started);

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
        using var stageTiming = BeginTiming(GraphicsStage.SegmentedSubmit);

        if (CommandStates.TryGetValue(list, out var state) && state.MemoryBoundaries.Count != 0)
        {
            using var completion = new HostCompletionFence();
            using var ordered = EnableOrderedTextureUploads && !EnableColorFlow && !EnableInputColorTrace &&
                !EnableTextureIntermediatePreview && !EnableGuestPreviewWindow
                ? new OrderedTextureUploads(queue, EnableBatchedTextureUploads) : null;

            foreach (var boundary in state.MemoryBoundaries)
            {
                ordered?.SubmitBeforeConsumer();
                var segment = boundary.Host;

                if (segment != 0)
                {
                    ((delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10))(queue, 1, &segment);
                }

                completion.Wait(queue);
                ordered?.CompleteAfterFence();

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
                    ExecuteVertexFetchDrawBoundary(queue, draw);
                }

                if (boundary.Rectangle is { } rectangle)
                {
                    ExecuteRectangleDrawBoundary(queue, rectangle, ordered);
                }

                if (boundary.Compute is { } compute)
                {
                    try
                    {
                        ExecuteColorLutCompute(queue, compute);
                    }
                    catch (Exception exception)
                    {
                        Environment.FailFast($"[graphics] R17 compute submission failed: {exception.Message}",
                            exception);
                    }
                }

                if (boundary.PlanarColor is { } planarColor)
                {
                    try
                    {
                        ExecutePlanarColorDrawBoundary(queue, planarColor, ordered);
                    }
                    catch (Exception exception)
                    {
                        Environment.FailFast($"[graphics] Planar color submission failed: {exception.Message}", exception);
                    }
                }

                if (boundary.IndexedUi is { } indexedUi)
                {
                    try
                    {
                        ExecuteIndexedUiBoundary(queue, indexedUi, ordered);
                    }
                    catch (Exception exception)
                    {
                        Environment.FailFast($"[graphics] Indexed UI submission failed: {exception.Message}", exception);
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
                        ExecuteTexturedDrawBoundary(queue, textured, ordered);
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
        using var stageTiming = BeginTiming(GraphicsStage.PublishWrite);

        var value = write.Value;

        switch (write.Bytes)
        {
            case 8 when write.Flags == 1:
            {
                using var calibrationTiming = BeginTiming(GraphicsStage.ClockCalibration);

                ulong gpu = 0, cpu = 0;
                // ID3D12CommandQueue::GetClockCalibration is slot 17.
                var resultCode = ((delegate* unmanaged[Stdcall]<nint, ulong*, ulong*, int>)Slot(queue, 17))(queue, &gpu, &cpu);

                if (resultCode < 0)
                {
                    Environment.FailFast($"[graphics] GPU clock calibration failed: 0x{resultCode:X8}.");
                }

                value = gpu;

                break;
            }
            case 8 when write.Flags == 2:
                value = (ulong)Stopwatch.GetTimestamp();

                break;
        }

        using (BeginTiming(GraphicsStage.MemoryPublication))
        {
            PublishMemory(write.Address, value, write.Bytes);
        }
    }

    private static void VerifyCommandMemoryAbi()
    {
        ValidateCommandMemoryRegistration();

        VerifyPageMappingPlan();

        VerifyTextureTexturePlan();

        var list = (nint)NativeMemory.AllocZeroed(0x980);

        try
        {
            ((GraphicsObjectHeader*)list)->Table = _listNew;

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
                                             || state.MemoryBoundaries[3].IsDrawPreparation
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

            VerifyVertexFetchBoundaryRecording(list, state);

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
}
