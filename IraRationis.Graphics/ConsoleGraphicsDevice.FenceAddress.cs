// SPDX-License-Identifier: GPL-3.0-or-later
// Guest fence address ABI cross-checked against VIctory/victory-fork fence.rs.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    // The existing console fence contains an aligned 64-bit completion value
    // at +0x10, creation flags at +0x18, and the private host pointer at +0x20.
    private const int FenceValueOffset = 0x10;
    private const int FenceAddressSlot = 12;
    private const int FenceMirrorIntervalMilliseconds = 2;
    private static readonly Lock FenceAddressGate = new();
    private static readonly Dictionary<ulong, nint> ExportedFenceAddresses = new();
    private static readonly AutoResetEvent FenceMirrorWake = new(false);
    private static Thread? _fenceMirrorWorker;
    private static int _exportedFenceCount;
    private static int _fenceAddressReported;

    private static ulong FenceValueAddress(nint fence)
    {
        return (nuint)(fence + FenceValueOffset);
    }

    private static bool IsLiveFence(nint fence)
    {
        return fence != 0 && LiveObjects.TryGetValue(fence, out var table) && table == _fenceTable;
    }

    // The native D3D12 fence remains authoritative. Publish the observed value,
    // not the last submitted value or a monotonic maximum (D3D12 fences can rewind).
    // Skip unchanged stores to reduce cache-line invalidation by the observer.
    private static ulong RefreshFenceMirror(nint fence)
    {
        var host = (ID3D12Fence*)Host(fence);
        var observed = host->GetCompletedValue();
        ref var mirror = ref *(ulong*)(fence + FenceValueOffset);
        if (Volatile.Read(ref mirror) != observed)
        {
            Volatile.Write(ref mirror, observed);
        }

        return observed;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong GetFenceGpuAddress(nint self)
    {
        lock (FenceAddressGate)
        {
            if (!IsLiveFence(self) || Host(self) == 0)
            {
                return 0;
            }

            var address = FenceValueAddress(self);
            if ((address & 7UL) != 0)
            {
                Environment.FailFast("[graphics] Fence value is not aligned to eight bytes.");
            }

            _ = RefreshFenceMirror(self);
            if (ExportedFenceAddresses.TryAdd(address, self))
            {
                Volatile.Write(ref _exportedFenceCount, ExportedFenceAddresses.Count);
                if (_fenceMirrorWorker == null)
                {
                    _fenceMirrorWorker = new Thread(RunFenceMirrorObserver)
                    {
                        IsBackground = true,
                        Name = "IRX fence address observer"
                    };
                    _fenceMirrorWorker.Start();
                }

                _ = FenceMirrorWake.Set();
            }

            if (Interlocked.Exchange(ref _fenceAddressReported, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[graphics] IRX-GFX-FENCE-R5 active: aligned guest fence mirror; host completion is authoritative.");
            }

            // This is a guest CPU-address alias, NOT a hardware D3D12 GPU VA.
            // It remains valid only while the guest owns a reference to the fence.
            return address;
        }
    }

    private static void RunFenceMirrorObserver()
    {
        while (true)
        {
            int delay;
            lock (FenceAddressGate)
            {
                foreach (var fence in ExportedFenceAddresses.Values)
                {
                    if (!IsLiveFence(fence))
                    {
                        Environment.FailFast("[graphics] Exported fence lifetime invariant violated.");
                    }

                    _ = RefreshFenceMirror(fence);
                }

                delay = ExportedFenceAddresses.Count == 0 ? Timeout.Infinite : FenceMirrorIntervalMilliseconds;
            }

            // One observer for the whole process, sleeping when no addresses
            // are exported. No per-signal allocations or GPU queue waits.
            _ = FenceMirrorWake.WaitOne(delay);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseAddressableFence(nint self)
    {
        lock (FenceAddressGate)
        {
            if (!IsLiveFence(self))
            {
                return 0;
            }

            var address = FenceValueAddress(self);
            // Final native release and observer access are serialized. Never
            // dereference the guest object after Drop reports zero references.
            var remaining = Drop(self);
            if (remaining == 0 && ExportedFenceAddresses.Remove(address))
            {
                Volatile.Write(ref _exportedFenceCount, ExportedFenceAddresses.Count);
                _ = FenceMirrorWake.Set();
            }

            return remaining;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong ReadAddressableFence(nint self)
    {
        lock (FenceAddressGate)
        {
            return IsLiveFence(self) ? RefreshFenceMirror(self) : ulong.MaxValue;
        }
    }

    private static int SignalFenceAndRefresh(nint self, ulong value)
    {
        var hr = ((ID3D12Fence*)Host(self))->Signal(value);
        if (hr >= 0)
        {
            _ = RefreshFenceMirror(self);
        }

        return hr;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SignalAddressableFence(nint self, ulong value)
    {
        lock (FenceAddressGate)
        {
            return IsLiveFence(self) ? SignalFenceAndRefresh(self, value) : InvalidArgument;
        }
    }

    private static bool TryResolveFenceAddress(ulong address, int bytes, out nint fence, out int shift)
    {
        // Caller holds FenceAddressGate. Only the complete value or an aligned
        // 32-bit half is supported; no unrelated bytes in the object are aliased.
        fence = 0;
        shift = (int)(address & 7UL) * 8;
        if ((bytes == 8 && shift != 0) || (bytes == 4 && shift is not (0 or 32)) || bytes is not (4 or 8))
        {
            return false;
        }

        return ExportedFenceAddresses.TryGetValue(address & ~7UL, out fence);
    }

    private static bool TryReadFenceAddress(ulong address, int bytes, out ulong value)
    {
        value = 0;
        if (Volatile.Read(ref _exportedFenceCount) == 0)
        {
            return false;
        }

        lock (FenceAddressGate)
        {
            if (!TryResolveFenceAddress(address, bytes, out var fence, out var shift))
            {
                return false;
            }

            var observed = RefreshFenceMirror(fence);
            value = bytes == 8 ? observed : (uint)(observed >> shift);

            return true;
        }
    }

    private static bool TryPublishFenceAddress(ulong address, ulong value, int bytes)
    {
        if (Volatile.Read(ref _exportedFenceCount) == 0)
        {
            return false;
        }

        lock (FenceAddressGate)
        {
            if (!TryResolveFenceAddress(address, bytes, out var fence, out var shift))
            {
                return false;
            }

            // The command-memory boundary has already waited for preceding GPU
            // work. A guest packet write to a fence must update its native fence
            // too, so subsequent host queue waits and host events can complete.
            if (bytes == 4)
            {
                var previous = RefreshFenceMirror(fence);
                if (previous == ulong.MaxValue)
                {
                    Environment.FailFast("[graphics] Cannot merge a partial fence write after device removal.");
                }

                var mask = (ulong)uint.MaxValue << shift;
                value = (previous & ~mask) | ((ulong)(uint)value << shift);
            }

            var hr = SignalFenceAndRefresh(fence, value);
            if (hr < 0)
            {
                Environment.FailFast(
                    $"[graphics] Deferred fence write failed: address=0x{address:X}, HRESULT=0x{hr:X8}.");
            }

            return true;
        }
    }

    private static void RegisterFenceAddressMethods()
    {
        var fence = (nint*)_fenceTable;
        fence[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseAddressableFence;
        fence[8] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&ReadAddressableFence;
        fence[10] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, int>)&SignalAddressableFence;
        fence[FenceAddressSlot] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&GetFenceGpuAddress;
    }

    private static void ValidateFenceAddressRegistration()
    {
        var fence = (nint*)_fenceTable;
        if (FenceValueOffset % sizeof(ulong) != 0 || FenceValueOffset + sizeof(ulong) > FenceHost ||
            fence[2] != (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseAddressableFence ||
            fence[8] != (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&ReadAddressableFence ||
            fence[10] != (nint)(delegate* unmanaged[Stdcall]<nint, ulong, int>)&SignalAddressableFence ||
            fence[FenceAddressSlot] != (nint)(delegate* unmanaged[Stdcall]<nint, ulong>)&GetFenceGpuAddress)
        {
            throw new InvalidOperationException("Addressable fence ABI registration failed.");
        }

        var getAddress = (delegate* unmanaged[Stdcall]<nint, ulong>)fence[FenceAddressSlot];
        if (getAddress(0) != 0)
        {
            throw new InvalidOperationException("Null fence address probe failed.");
        }

        Console.WriteLine("[graphics-probe] Fence address registration passed (CPU ABI only).");
    }

    internal static void VerifyFenceAddressProbeCleanup()
    {
        lock (FenceAddressGate)
        {
            if (ExportedFenceAddresses.Count != 0 || Volatile.Read(ref _exportedFenceCount) != 0)
            {
                throw new InvalidOperationException("Released fence remains registered with the address observer.");
            }
        }

        Console.WriteLine("[graphics-probe] Addressable fence release and observer registry cleanup passed.");
    }

    // Invoked from DeviceProbe for both queue ABIs. A blocked native queue is
    // used to distinguish submitted values from actual GPU completion.
    internal static void VerifyFenceAddressHostProbe(nint device, nint queue, nint fence)
    {
        var getAddress = (delegate* unmanaged[Stdcall]<nint, ulong>)Slot(fence, FenceAddressSlot);
        var cpuSignal = (delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(fence, 10);
        var address = getAddress(fence);
        if (address == 0 || address != getAddress(fence) || (address & 7UL) != 0 ||
            Volatile.Read(ref *(ulong*)(nuint)address) != 0)
        {
            throw new InvalidOperationException("Guest fence address is not stable, aligned, and initialized.");
        }

        var create = (delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device, 36);
        var id = FenceId;
        nint gate = 0;
        CheckHostResult(create(device, 0, 0, &id, &gate), "Fence address probe gate");
        var newer = Table(queue) == _queueNew;
        var queueWait = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(queue, newer ? 17 : 15);
        var queueSignal = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(queue, newer ? 16 : 14);
        var gateSignal = (delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(gate, 10);
        try
        {
            CheckHostResult(queueWait(queue, gate, 1), "Fence address probe queue gate");
            CheckHostResult(queueSignal(queue, fence, 7), "Fence address probe queued signal");
            if (Volatile.Read(ref *(ulong*)(nuint)address) != 0)
            {
                throw new InvalidOperationException("Fence mirror published an incomplete GPU signal.");
            }

            CheckHostResult(gateSignal(gate, 1), "Fence address probe open gate");
            var started = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref *(ulong*)(nuint)address) != 7)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                {
                    Environment.FailFast(
                        "[graphics-probe] Addressable fence GPU completion did not reach guest memory.");
                }

                Thread.Sleep(1);
            }

            const ulong written = 0x1122334455667788UL;
            PublishMemory(address, written, 8);
            if (((ID3D12Fence*)Host(fence))->GetCompletedValue() != written ||
                Volatile.Read(ref *(ulong*)(nuint)address) != written)
            {
                throw new InvalidOperationException("Deferred fence publication did not update both domains.");
            }

            PublishMemory(address + 4, 0xAABBCCDD, 4);
            const ulong merged = 0xAABBCCDD55667788UL;
            if (!TryReadMemory(address, 8, out var observed) || observed != merged ||
                !TryReadMemory(address + 4, 4, out var upper) || upper != 0xAABBCCDD ||
                ((ID3D12Fence*)Host(fence))->GetCompletedValue() != merged)
            {
                throw new InvalidOperationException("Partial fence address write/read failed.");
            }

            // Native fences may rewind; leave the existing graphics probe's
            // fence at its original value for its later event-at-42 test.
            CheckHostResult(cpuSignal(fence, 0), "Fence address probe rewind");
            if (Volatile.Read(ref *(ulong*)(nuint)address) != 0)
            {
                throw new InvalidOperationException("Fence mirror did not reflect a rewind.");
            }
        }
        finally
        {
            // Never leave a GPU wait blocked or destroy its fence while queued.
            var hr = gateSignal(gate, 1);
            if (hr < 0)
            {
                Environment.FailFast($"[graphics-probe] Fence address gate release failed: 0x{hr:X8}.");
            }

            WaitForHostCompletion(Host(queue));
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(gate, 2))(gate);
        }

        Console.WriteLine(
            "[graphics-probe] Fence address, delayed native queue completion, deferred publication and rewind passed.");
    }
}
