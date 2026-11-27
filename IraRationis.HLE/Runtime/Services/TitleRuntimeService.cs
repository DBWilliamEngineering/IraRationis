// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services;

internal sealed unsafe class TitleRuntimeService
{
    internal static readonly Guid Provider = new("973a344e-24bf-4d0f-8457-56c534892b29");
    internal static readonly Guid Interface = new("2549f142-6419-4a06-97b5-931aab7c2f34");
    private readonly RuntimeServiceObject _object;
    private readonly uint _titleId;
    private readonly RuntimeServiceTrace _trace;

    internal TitleRuntimeService(uint titleId, RuntimeServiceTrace trace)
    {
        _titleId = titleId;
        _trace = trace;
        _object = new RuntimeServiceObject(this,
        [
            0, 0, 0,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetTitleId,
            (nint)(delegate* unmanaged[Stdcall]<nint, byte*, byte*, nint, void>)&LaunchNewGame
        ], Interface);
    }

    internal bool TryQuery(Guid requested, out nint address)
    {
        address = requested == Interface ? _object.Retain() : 0;

        return address != 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTitleId(nint self, uint* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = RuntimeServiceObject.Owner<TitleRuntimeService>(self)._titleId;

        return RuntimeServiceResult.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void LaunchNewGame(nint self, byte* executable, byte* arguments, nint user)
    {
        RuntimeServiceObject.Owner<TitleRuntimeService>(self)._trace.Write("LaunchNewGameUnsupported",
            "launched=False; userHandleForwarded=False");
    }
}
