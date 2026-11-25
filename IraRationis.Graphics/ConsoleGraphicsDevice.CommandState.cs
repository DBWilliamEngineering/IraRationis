// SPDX-License-Identifier: GPL-3.0-or-later
// ABI adapted from DexNom/VIctory crates/victory-gpu/src/list.rs and object.rs.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const uint MaximumRootParameters = 64;
    private const uint MaximumRootWords = 64;
    private const uint MaximumVertexViews = 32;
    private static readonly ConcurrentDictionary<nint, RecordedCommandState> CommandStates = new();

    private static readonly int[] CommandStateSlots =
        [25, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 50];

    private static nint AllocationCallbackAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<int>)&AcceptAllocatorCallback;
        }
    }

    private static RecordedCommandState StateFor(nint self)
    {
        // D3D12 command lists require external synchronization while recording.
        if (!LiveObjects.TryGetValue(self, out var table) || (table != _listOld && table != _listNew) ||
            *(int*)(self + ListClosed) != 0)
        {
            Environment.FailFast("[graphics] Binding attempted on an invalid or closed command list.");
        }

        return CommandStates.GetOrAdd(self, static _ => new RecordedCommandState());
    }

    private static void ReleaseCommandState(nint self)
    {
        if (CommandStates.TryRemove(self, out var state))
        {
            foreach (var boundary in state.MemoryBoundaries)
            {
                if (boundary.Host != 0)
                {
                    _ = HostRelease(boundary.Host);
                }
            }

            foreach (var value in state.Owned)
            {
                _ = Drop(value);
            }
        }
    }

    private static RecordedRootState RootFor(RecordedCommandState state, bool compute)
    {
        return compute ? state.Compute : state.Graphics;
    }

    private static void BindPipelineCore(nint self, nint pipeline)
    {
        var state = StateFor(self);
        if (pipeline != 0 && !IsPipeline(pipeline))
        {
            Environment.FailFast("[graphics] Invalid console pipeline binding.");
        }

        state.RetainObject(pipeline);
        state.Pipeline = pipeline;
        InspectBoundPipeline(pipeline);
        // Never forward a CPU-only console pipeline to the Windows D3D12 device.
    }

    private static void BindRootCore(nint self, nint root, bool compute)
    {
        var state = StateFor(self);
        if (root != 0 && !IsRootSignature(root))
        {
            Environment.FailFast("[graphics] Invalid console root signature binding.");
        }

        state.RetainObject(root);
        var binding = RootFor(state, compute);
        if (binding.Signature != root)
        {
            binding.ClearArguments();
            binding.Signature = root;
        }
    }

    private static void BindTableCore(nint self, uint slot, ulong handle, bool compute)
    {
        if (slot >= MaximumRootParameters)
        {
            Environment.FailFast("[graphics] Root descriptor table index exceeds supported limit.");
        }

        RootFor(StateFor(self), compute).Tables[slot] = handle;
    }

    private static void BindConstantsCore(nint self, uint slot, uint count, uint* values, uint offset, bool compute)
    {
        if (slot >= MaximumRootParameters || offset > MaximumRootWords || count > MaximumRootWords - offset ||
            (count != 0 && values == null))
        {
            Environment.FailFast("[graphics] Invalid root constants range.");
        }

        if (count == 0)
        {
            return;
        }

        var constants = RootFor(StateFor(self), compute).Constants;
        if (!constants.TryGetValue(slot, out var words))
        {
            words = new uint[MaximumRootWords];
            constants.Add(slot, words);
        }

        new ReadOnlySpan<uint>(values, (int)count).CopyTo(words.AsSpan((int)offset));
    }

    private static void BindViewCore(nint self, uint slot, ulong address, bool compute, uint kind)
    {
        if (slot >= MaximumRootParameters)
        {
            Environment.FailFast("[graphics] Root view index exceeds supported limit.");
        }

        RootFor(StateFor(self), compute).Views[slot] = (kind, address);
    }

    // Both VIctory and DexNom ignore this console command allocator setting.
    // Windows x64 permits extra unused integer arguments. No callback is invoked.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AcceptAllocatorCallback()
    {
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordPipeline(nint self, nint pipeline)
    {
        BindPipelineCore(self, pipeline);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordDescriptorHeaps(nint self, uint count, nint* heaps)
    {
        if (count > 2 || (count != 0 && heaps == null))
        {
            Environment.FailFast("[graphics] Invalid shader descriptor heap count.");
        }

        var state = StateFor(self);
        var next = new ReadOnlySpan<nint>(heaps, (int)count).ToArray();
        foreach (var heap in next)
        {
            if (!IsDescriptorHeap(heap))
            {
                Environment.FailFast("[graphics] Invalid shader descriptor heap.");
            }

            state.RetainObject(heap);
        }

        if (!state.Heaps.AsSpan().SequenceEqual(next))
        {
            state.Compute.Tables.Clear();
            state.Graphics.Tables.Clear();
            state.Heaps = next;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordIndexBuffer(nint self, RecordedBufferView* view)
    {
        StateFor(self).Index = view == null ? null : *view;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordVertexBuffers(nint self, uint start, uint count, RecordedBufferView* views)
    {
        if (start > MaximumVertexViews || count > MaximumVertexViews - start)
        {
            Environment.FailFast("[graphics] Invalid vertex buffer range.");
        }

        var state = StateFor(self);
        for (var index = 0U; index < count; index++)
        {
            if (views == null)
            {
                _ = state.Vertices.Remove(start + index);
            }
            else
            {
                state.Vertices[start + index] = views[index];
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordRenderTargets(nint self, uint count, ulong* handles, int oneRange, ulong* depth)
    {
        if (count > 8 || (count != 0 && handles == null))
        {
            Environment.FailFast("[graphics] Invalid render target count.");
        }

        var state = StateFor(self);
        var targets = new ulong[count];
        for (var index = 0U; index < count; index++)
        {
            // Console RTV descriptor stride, not the host D3D12 stride.
            if (oneRange != 0 && *handles > ulong.MaxValue - 128UL * index)
            {
                Environment.FailFast("[graphics] Render target handle overflow.");
            }

            targets[index] = oneRange != 0 ? *handles + 128UL * index : handles[index];
        }

        state.Targets = targets;
        state.Depth = depth == null ? null : *depth;
        if (Host(self) != 0)
        {
            foreach (var target in targets)
            {
                MaterializeBoundTarget(state, target, 20);
            }

            if (state.Depth is { } depthHandle)
            {
                MaterializeBoundTarget(state, depthHandle, 21);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootSignature(nint self, nint root)
    {
        BindRootCore(self, root, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootTable(nint self, uint slot, ulong handle)
    {
        BindTableCore(self, slot, handle, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootConstant(nint self, uint slot, uint value, uint offset)
    {
        BindConstantsCore(self, slot, 1, &value, offset, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootConstants(nint self, uint slot, uint count, uint* values, uint offset)
    {
        BindConstantsCore(self, slot, count, values, offset, true);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootCbv(nint self, uint slot, ulong address)
    {
        BindViewCore(self, slot, address, true, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootSrv(nint self, uint slot, ulong address)
    {
        BindViewCore(self, slot, address, true, 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordComputeRootUav(nint self, uint slot, ulong address)
    {
        BindViewCore(self, slot, address, true, 2);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootSignature(nint self, nint root)
    {
        BindRootCore(self, root, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootTable(nint self, uint slot, ulong handle)
    {
        BindTableCore(self, slot, handle, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootConstant(nint self, uint slot, uint value, uint offset)
    {
        BindConstantsCore(self, slot, 1, &value, offset, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootConstants(nint self, uint slot, uint count, uint* values, uint offset)
    {
        BindConstantsCore(self, slot, count, values, offset, false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootCbv(nint self, uint slot, ulong address)
    {
        BindViewCore(self, slot, address, false, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootSrv(nint self, uint slot, ulong address)
    {
        BindViewCore(self, slot, address, false, 1);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RecordGraphicsRootUav(nint self, uint slot, ulong address)
    {
        BindViewCore(self, slot, address, false, 2);
    }

    private static void RegisterCommandStateMethods(nint* table)
    {
        table[25] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, void>)&RecordPipeline;
        table[32] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)&RecordDescriptorHeaps;
        table[33] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, void>)&RecordComputeRootSignature;
        table[34] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, void>)&RecordGraphicsRootSignature;
        table[35] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordComputeRootTable;
        table[36] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordGraphicsRootTable;
        table[37] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)&RecordComputeRootConstant;
        table[38] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)&RecordGraphicsRootConstant;
        table[39] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint, void>)&RecordComputeRootConstants;
        table[40] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint, void>)&RecordGraphicsRootConstants;
        table[41] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordComputeRootCbv;
        table[42] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordGraphicsRootCbv;
        table[43] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordComputeRootSrv;
        table[44] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordGraphicsRootSrv;
        table[45] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordComputeRootUav;
        table[46] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)&RecordGraphicsRootUav;
        table[47] = (nint)(delegate* unmanaged[Stdcall]<nint, RecordedBufferView*, void>)&RecordIndexBuffer;
        table[48] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, RecordedBufferView*, void>)&RecordVertexBuffers;
        table[50] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, ulong*, int, ulong*, void>)&RecordRenderTargets;
    }

    private static void ValidateCommandStateRegistration()
    {
        if (((nint*)_allocatorTable)[9] != AllocationCallbackAddress || sizeof(RecordedBufferView) != 16)
        {
            throw new InvalidOperationException("Allocator callback or buffer view ABI registration failed.");
        }

        foreach (var slot in CommandStateSlots)
        {
            if (((nint*)_listOld)[slot] == MissingAddress(slot) || ((nint*)_listNew)[slot] != ((nint*)_listOld)[slot])
            {
                throw new InvalidOperationException($"Command state ABI registration failed at slot 0x{slot * 8:X}.");
            }
        }
    }

    private static string? CommandStateStatus(string kind, int slot)
    {
        if (kind == "allocator" && slot == 9)
        {
            return "registered-compatibility-noop";
        }

        if ((kind == "list-old" || kind == "list-new") && Array.IndexOf(CommandStateSlots, slot) >= 0)
        {
            return "registered-cpu-only";
        }

        return null;
    }

    private static void VerifyCommandStateAbi()
    {
        var callback = (delegate* unmanaged[Stdcall]<int>)((nint*)_allocatorTable)[9];
        if (callback() != 0)
        {
            throw new InvalidOperationException("Allocator callback acceptance failed.");
        }

        // Synthetic header only: no host device is needed, and Drop is never called on it.
        var list = (nint)NativeMemory.AllocZeroed(0x980);
        try
        {
            ((Header*)list)->Table = _listNew;
            LiveObjects[list] = _listNew;
            var table = (nint*)_listNew;
            var constants = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint, void>)table[39];
            var words = stackalloc uint[2] { 7, 9 };
            constants(list, 3, 2, words, 4);
            words[0] = 99;
            var single = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)table[37];
            single(list, 3, 11, 5);
            var bindView = (delegate* unmanaged[Stdcall]<nint, uint, ulong, void>)table[46];
            bindView(list, 2, 0x10000);
            var targets = (delegate* unmanaged[Stdcall]<nint, uint, ulong*, int, ulong*, void>)table[50];
            var first = 0x20000UL;
            targets(list, 2, &first, 1, null);
            var state = StateFor(list);
            if (state.Compute.Constants[3][4] != 7 || state.Compute.Constants[3][5] != 11 ||
                state.Graphics.Views[2] != (2U, 0x10000UL) || state.Targets[1] != 0x20080)
            {
                throw new InvalidOperationException("Command state snapshot ABI failed.");
            }

            ReleaseCommandState(list);
            if (CommandStates.ContainsKey(list))
            {
                throw new InvalidOperationException("Command state reset cleanup failed.");
            }
        }
        finally
        {
            ReleaseCommandState(list);
            _ = LiveObjects.TryRemove(list, out _);
            NativeMemory.Free((void*)list);
        }

        Console.WriteLine("[graphics-probe] Allocator callback and command-list state ABI passed.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecordedBufferView
    {
        public ulong Address;
        public uint Size;
        public uint FormatOrStride;
    }

    private sealed class RecordedRootState
    {
        public readonly Dictionary<uint, uint[]> Constants = new();
        public readonly Dictionary<uint, ulong> Tables = new();
        public readonly Dictionary<uint, (uint Kind, ulong Address)> Views = new();
        public nint Signature;

        public void ClearArguments()
        {
            Tables.Clear();
            Constants.Clear();
            Views.Clear();
        }
    }

    private sealed class RecordedCommandState
    {
        public readonly RecordedRootState Compute = new();
        public readonly RecordedRootState Graphics = new();
        public readonly List<MemoryBoundary> MemoryBoundaries = new();
        public readonly HashSet<nint> Owned = new();
        public readonly Dictionary<uint, RecordedBufferView> Vertices = new();
        public float[]? Blend;
        public bool BlendSet;
        public ulong? Depth;
        public nint[] Heaps = [];
        public RecordedBufferView? Index;
        public nint Pipeline;
        public RECT[]? Scissors;
        public uint? Stencil;
        public ulong[] Targets = [];
        public uint? Topology;
        public D3D12_VIEWPORT[]? Viewports;

        public void RetainObject(nint value)
        {
            if (value != 0 && Owned.Add(value))
            {
                _ = Retain(value);
            }
        }
    }
}
