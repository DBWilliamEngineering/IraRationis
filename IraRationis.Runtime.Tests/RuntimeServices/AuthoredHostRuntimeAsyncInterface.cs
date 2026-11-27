// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal sealed unsafe class AuthoredHostRuntimeAsyncInterface : IDisposable
{
    private nint* _instance;
    private nint* _methods;

    internal AuthoredHostRuntimeAsyncInterface()
    {
        _methods = (nint*)NativeMemory.AllocZeroed(19, (nuint)sizeof(nint));
        _instance = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        *_instance = (nint)_methods;

        _methods[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AuthoredHostRuntimeAsyncTargets.AddReference;
        _methods[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AuthoredHostRuntimeAsyncTargets.ReleaseReference;
        _methods[7] =
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nint, byte*, nint, int>)
            &AuthoredHostRuntimeAsyncTargets.Begin;
        _methods[9] =
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, uint, int>)&AuthoredHostRuntimeAsyncTargets
                .Schedule;
        _methods[10] =
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int, nuint, void>)&AuthoredHostRuntimeAsyncTargets
                .Complete;
        _methods[11] =
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nuint, nint, nuint*, int>)
            &AuthoredHostRuntimeAsyncTargets.GetResult;
        _methods[18] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, int>)&AuthoredHostRuntimeAsyncTargets
                .SubmitCallback;

        AuthoredHostRuntimeAsyncTargets.Reset();
    }

    internal nint Address
    {
        get
        {
            return (nint)_instance;
        }
    }

    public void Dispose()
    {
        NativeMemory.Free(_instance);
        NativeMemory.Free(_methods);
        _instance = null;
        _methods = null;
    }

    internal void RemoveMethod(int slot)
    {
        _methods[slot] = 0;
    }
}
