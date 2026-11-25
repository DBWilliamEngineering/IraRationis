// SPDX-License-Identifier: GPL-3.0-or-later
// Console synchronization signature from Zettrox/ztrx victory-gpu/src/list.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly Guid EnhancedListId = new("dd171223-8b61-4769-90e3-160ccde4e2c1");
    private static int _reportedPipelineSync;

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

        if (Interlocked.Exchange(ref _reportedPipelineSync, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[graphics] Recorded pipeline sync: flags=0x{flags:X}, control=0x{control:X}, address=0x{address:X}, size=0x{size:X} (global barrier).");
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
