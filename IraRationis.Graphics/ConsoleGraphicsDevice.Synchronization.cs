// SPDX-License-Identifier: GPL-3.0-or-later
// Console synchronization signatures from Zettrox/ztrx and DexNom/VIctory victory-gpu/src/list.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly Guid EnhancedListId = new("dd171223-8b61-4769-90e3-160ccde4e2c1");
    private static int _reportedPipelineSync;
    private static int _reportedEnhancedGroups;
    private static int _reportedLegacyBarriers;

    private static nint PipelineSyncAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, ulong, ulong, void>)&PipelineSync;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void PipelineSync(nint self, uint flags, uint control, ulong address, ulong size)
    {
        RecordConservativeGlobalBarrier(self);

        if (Interlocked.Exchange(ref _reportedPipelineSync, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] Recorded pipeline sync: flags=0x{flags:X}, control=0x{control:X}, address=0x{address:X}, size=0x{size:X} (global barrier).");
        }
    }

    private static void RecordConservativeGlobalBarrier(nint self)
    {
        if (!IsList(self) || *(int*)(self + ListClosed) != 0)
        {
            Environment.FailFast("[graphics] Pipeline synchronization requires an open command list.");
        }

        // Enhanced barriers are available in the inbox Windows 11 22H2 runtime.
        // This probe does not install or configure an older OS Agility SDK.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            Environment.FailFast(
                "[graphics] Pipeline synchronization requires Windows 11 22H2 or later in this revision.");

            return;
        }

        var enhanced = *(nint*)(self + ListEnhancedHost);

        if (enhanced == 0)
        {
            var options = default(D3D12_FEATURE_DATA_D3D12_OPTIONS12);
            var check = (delegate* unmanaged[Stdcall]<nint, uint, void*, uint, int>)Slot((nint)_host, 13);

            var result = check((nint)_host, (uint)D3D12_FEATURE.D3D12_FEATURE_D3D12_OPTIONS12,
                &options, (uint)sizeof(D3D12_FEATURE_DATA_D3D12_OPTIONS12));

            if (result < 0 || (int)options.EnhancedBarriersSupported == 0)
            {
                Environment.FailFast("[graphics] Host driver does not support D3D12 enhanced barriers.");
            }

            var host = Host(self);
            var iid = EnhancedListId;
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(host, 0);

            result = query(host, &iid, &enhanced);

            if (result < 0 || enhanced == 0)
            {
                Environment.FailFast($"[graphics] ID3D12GraphicsCommandList7 unavailable: 0x{result:X8}.");
            }

            *(nint*)(self + ListEnhancedHost) = enhanced;
        }

        // Until the console flags and address-range rules are decoded, conservatively
        // synchronize all work in this queue. This does not transition texture layouts,
        // synchronize other queues, or make arbitrary guest addresses GPU-visible.
        var barrier = new D3D12_GLOBAL_BARRIER
        {
            SyncBefore = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
            SyncAfter = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
            AccessBefore = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON,
            AccessAfter = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON
        };
        var group = new GlobalBarrierGroup
        {
            Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_GLOBAL,
            Count = 1,
            Barriers = &barrier
        };

        if (sizeof(GlobalBarrierGroup) != sizeof(D3D12_BARRIER_GROUP))
        {
            Environment.FailFast("[graphics] Unexpected enhanced barrier group layout.");
        }

        ((ID3D12GraphicsCommandList7*)enhanced)->Barrier(1, (D3D12_BARRIER_GROUP*)&group);
    }

    // Guest barrier records contain console resource pointers and layouts. Do not
    // reinterpret them as host D3D12 structures. Until resource state tracking is
    // implemented, collapse each nonempty batch into a host global ordering barrier.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void EnhancedBarrierGroups(nint self, uint count, void* groups)
    {
        RecordBarrierBatch(self, count, groups, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void LegacyResourceBarriers(nint self, uint count, void* barriers)
    {
        RecordBarrierBatch(self, count, barriers, false);
    }

    private static void RecordBarrierBatch(nint self, uint count, void* records, bool enhanced)
    {
        if (count == 0)
        {
            return;
        }

        if (records == null)
        {
            Environment.FailFast("[graphics] Nonempty resource barrier batch has a null record pointer.");
        }

        RecordConservativeGlobalBarrier(self);

        ref var reported = ref enhanced ? ref _reportedEnhancedGroups : ref _reportedLegacyBarriers;

        var calls = Interlocked.Increment(ref reported);

        if (calls <= 4 || (calls & (calls - 1)) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] {(enhanced ? "EnhancedBarrierGroups" : "ResourceBarrier")}: count={count}, call={calls} (global ordering only; guest layouts not translated).");
        }
    }

    private static void RegisterBarrierMethods(nint* table, bool newer)
    {
        table[26] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void*, void>)&LegacyResourceBarriers;

        if (newer)
        {
            table[146] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void*, void>)&EnhancedBarrierGroups;
        }
    }

    private static void ValidateBarrierRegistration()
    {
        var legacy = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void*, void>)&LegacyResourceBarriers;
        var enhanced = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void*, void>)&EnhancedBarrierGroups;

        if (((nint*)_listOld)[26] != legacy || ((nint*)_listNew)[26] != legacy ||
            ((nint*)_listNew)[146] != enhanced || ((nint*)_listOld)[146] != MissingAddress(146))
        {
            throw new InvalidOperationException("Resource barrier ABI registration failed.");
        }

        // Empty batches must never dereference a record pointer or query the GPU.
        ((delegate* unmanaged[Stdcall]<nint, uint, void*, void>)legacy)(0, 0, null);

        ((delegate* unmanaged[Stdcall]<nint, uint, void*, void>)enhanced)(0, 0, null);
    }

    private static string? BarrierStatus(string kind, int slot)
    {
        return ((kind == "list-old" || kind == "list-new") && slot == 26) ||
               (kind == "list-new" && slot == 146)
            ? "registered-conservative-barrier"
            : null;
    }

    // Run through the console vtable on the actual host list. The probe checks
    // recording/submission, not guest resource transitions or cache coherence.
    internal static void VerifyBarrierRecording(nint list, bool newer)
    {
        var legacy = (delegate* unmanaged[Stdcall]<nint, uint, void*, void>)Slot(list, 26);
        var legacyRecord = new HostTransition { Type = 2 }; // UAV barrier, null resource.
        legacy(list, 0, null);

        legacy(list, 1, &legacyRecord);

        if (newer)
        {
            var barrier = new D3D12_GLOBAL_BARRIER
            {
                SyncBefore = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
                SyncAfter = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
                AccessBefore = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON,
                AccessAfter = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON
            };
            var group = new GlobalBarrierGroup
            {
                Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_GLOBAL,
                Count = 1,
                Barriers = &barrier
            };
            var enhanced = (delegate* unmanaged[Stdcall]<nint, uint, void*, void>)Slot(list, 146);

            enhanced(list, 0, null);

            enhanced(list, 1, &group);
        }
    }

    // The SDK union has a pointer-sized member at offset 8 on x64.
    // Keep its representation explicit instead of depending on a generated union member name.
    [StructLayout(LayoutKind.Sequential)]
    private struct GlobalBarrierGroup
    {
        public D3D12_BARRIER_TYPE Type;
        public uint Count;
        public D3D12_GLOBAL_BARRIER* Barriers;
    }
}
