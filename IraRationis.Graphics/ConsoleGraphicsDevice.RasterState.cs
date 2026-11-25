// SPDX-License-Identifier: GPL-3.0-or-later
// Console method signatures from Zettrox/ztrx victory-gpu/src/list.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
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
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(host, 20))(host, topology);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetViewports(nint self, uint count, D3D12_VIEWPORT* values)
    {
        var host = OpenGraphicsList(self);
        if (count > 16 || (count != 0 && values == null))
        {
            Environment.FailFast("[graphics] Invalid viewport array.");
        }

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

        ((delegate* unmanaged[Stdcall]<nint, uint, RECT*, void>)Slot(host, 22))(host, count, values);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetBlendFactor(nint self, float* factor)
    {
        var host = OpenGraphicsList(self);
        ((delegate* unmanaged[Stdcall]<nint, float*, void>)Slot(host, 23))(host, factor);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetStencilReference(nint self, uint reference)
    {
        var host = OpenGraphicsList(self);
        ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(host, 24))(host, reference);
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
