// SPDX-License-Identifier: GPL-3.0-or-later
// Console method signatures from Zettrox/ztrx victory-gpu/src/list.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using IraRationis.Graphics.Commands;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const uint RectangleRectangleTopology = 17;
    private const uint TriangleStripTopology = 5;

    private static uint HostRasterTopology(uint guest)
    {
        return guest == RectangleRectangleTopology ? 4U : guest;
    }

    private static nint OpenGraphicsList(nint self)
    {
        if (!IsList(self) || *(int*)(self + ListClosed) != 0 || *(uint*)(self + 0x10) != 0)
        {
            Environment.FailFast("[graphics] Raster state requires an open direct command list.");
        }

        return Host(self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetTopology(nint self, uint topology)
    {
        var host = OpenGraphicsList(self);

        StateFor(self).Topology = topology;

        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(host, 20))(host, HostRasterTopology(topology));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetViewports(nint self, uint count, D3D12_VIEWPORT* values)
    {
        var host = OpenGraphicsList(self);

        if (count > 16 || (count != 0 && values == null))
        {
            Environment.FailFast("[graphics] Invalid viewport array.");
        }

        StateFor(self).Viewports = new ReadOnlySpan<D3D12_VIEWPORT>(values, (int)count).ToArray();

        ((delegate* unmanaged[Stdcall]<nint, uint, D3D12_VIEWPORT*, void>)Slot(host, 21))(host, count, values);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetScissors(nint self, uint count, RECT* values)
    {
        var host = OpenGraphicsList(self);

        if (count > 16 || (count != 0 && values == null))
        {
            Environment.FailFast("[graphics] Invalid scissor array.");
        }

        StateFor(self).Scissors = new ReadOnlySpan<RECT>(values, (int)count).ToArray();

        ((delegate* unmanaged[Stdcall]<nint, uint, RECT*, void>)Slot(host, 22))(host, count, values);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetBlendFactor(nint self, float* factor)
    {
        var host = OpenGraphicsList(self);
        var state = StateFor(self);

        state.Blend = factor == null ? null : new ReadOnlySpan<float>(factor, 4).ToArray();

        state.BlendSet = true;

        ((delegate* unmanaged[Stdcall]<nint, float*, void>)Slot(host, 23))(host, factor);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetStencilReference(nint self, uint reference)
    {
        var host = OpenGraphicsList(self);

        StateFor(self).Stencil = reference;

        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(host, 24))(host, reference);
    }

    private static void ReplayRasterState(nint self, RecordedCommandState state)
    {
        if (*(uint*)(self + 0x10) != 0)
        {
            return;
        }

        var host = Host(self);

        if (state.Topology is { } topology)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(host, 20))(host, HostRasterTopology(topology));
        }

        if (state.Viewports is { } viewports)
        {
            fixed (D3D12_VIEWPORT* values = viewports)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, D3D12_VIEWPORT*, void>)Slot(host, 21))(host,
                    (uint)viewports.Length, values);
            }
        }

        if (state.Scissors is { } scissors)
        {
            fixed (RECT* values = scissors)
            {
                ((delegate* unmanaged[Stdcall]<nint, uint, RECT*, void>)Slot(host, 22))(host, (uint)scissors.Length,
                    values);
            }
        }

        if (state.BlendSet)
        {
            fixed (float* values = state.Blend)
            {
                ((delegate* unmanaged[Stdcall]<nint, float*, void>)Slot(host, 23))(host, values);
            }
        }

        if (state.Stencil is { } stencil)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(host, 24))(host, stencil);
        }
    }

    private static void RegisterRasterMethods(nint* table)
    {
        table[20] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void>)&SetTopology;

        table[21] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, D3D12_VIEWPORT*, void>)&SetViewports;

        table[22] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, RECT*, void>)&SetScissors;

        table[23] = (nint)(delegate* unmanaged[Stdcall]<nint, float*, void>)&SetBlendFactor;

        table[24] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, void>)&SetStencilReference;
    }
}
