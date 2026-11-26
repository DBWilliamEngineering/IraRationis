// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.HLE.Runtime.Services.Async;

internal sealed unsafe class HostRuntimeAsyncService : IDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly delegate* unmanaged[Stdcall]<nint, uint> _addReference;
    private readonly delegate* unmanaged[Stdcall]<nint, uint> _releaseReference;
    private readonly delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nint, byte*, nint, int> _begin;
    private readonly delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, uint, int> _schedule;
    private readonly delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int, nuint, void> _complete;
    private readonly delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nuint, nint, nuint*, int> _getResult;
    private nint _interfaceObject;

    // A successful construction takes ownership of one ABI-verified QueryApi reference.
    internal HostRuntimeAsyncService(nint interfaceObject)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The host runtime async service requires Windows x64.");
        }

        if (interfaceObject == 0)
        {
            throw new ArgumentException("An owned runtime async interface is required.", nameof(interfaceObject));
        }

        var methods = *(nint**)interfaceObject;

        if (methods == null || methods[1] == 0 || methods[2] == 0 || methods[7] == 0 ||
            methods[9] == 0 || methods[10] == 0 || methods[11] == 0)
        {
            throw new ArgumentException("The runtime async interface is missing a required method.", nameof(interfaceObject));
        }

        _addReference = (delegate* unmanaged[Stdcall]<nint, uint>)methods[1];
        _releaseReference = (delegate* unmanaged[Stdcall]<nint, uint>)methods[2];
        _begin = (delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nint, byte*, nint, int>)methods[7];
        _schedule = (delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, uint, int>)methods[9];
        _complete = (delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int, nuint, void>)methods[10];
        _getResult = (delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nuint, nint, nuint*, int>)methods[11];
        _interfaceObject = interfaceObject;
    }

    internal int Begin(XAsyncBlock* asyncBlock, nint context, nint identity, byte* identityName, nint providerCallback)
    {
        var interfaceObject = RetainForCall();

        try
        {
            return _begin(interfaceObject, asyncBlock, context, identity, identityName, providerCallback);
        }
        finally
        {
            ReleaseAfterCall(interfaceObject);
        }
    }

    internal int Schedule(XAsyncBlock* asyncBlock, uint delayMilliseconds)
    {
        var interfaceObject = RetainForCall();

        try
        {
            return _schedule(interfaceObject, asyncBlock, delayMilliseconds);
        }
        finally
        {
            ReleaseAfterCall(interfaceObject);
        }
    }

    internal void Complete(XAsyncBlock* asyncBlock, int result, nuint requiredBufferSize)
    {
        var interfaceObject = RetainForCall();

        try
        {
            _complete(interfaceObject, asyncBlock, result, requiredBufferSize);
        }
        finally
        {
            ReleaseAfterCall(interfaceObject);
        }
    }

    internal int GetResult(XAsyncBlock* asyncBlock, nint identity, nuint bufferSize, nint buffer, nuint* bufferUsed)
    {
        var interfaceObject = RetainForCall();

        try
        {
            return _getResult(interfaceObject, asyncBlock, identity, bufferSize, buffer, bufferUsed);
        }
        finally
        {
            ReleaseAfterCall(interfaceObject);
        }
    }

    public void Dispose()
    {
        var errors = NativeErrorState.Capture();

        try
        {
            nint interfaceObject;

            lock (_lifetimeGate)
            {
                interfaceObject = _interfaceObject;
                _interfaceObject = 0;
            }

            if (interfaceObject != 0)
            {
                _releaseReference(interfaceObject);
            }
        }
        finally
        {
            errors.Restore();
        }
    }

    private nint RetainForCall()
    {
        var errors = NativeErrorState.Capture();

        try
        {
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_interfaceObject == 0, this);

                _addReference(_interfaceObject);

                return _interfaceObject;
            }
        }
        finally
        {
            errors.Restore();
        }
    }

    private void ReleaseAfterCall(nint interfaceObject)
    {
        var errors = NativeErrorState.Capture();

        try
        {
            _releaseReference(interfaceObject);
        }
        finally
        {
            errors.Restore();
        }
    }
}
