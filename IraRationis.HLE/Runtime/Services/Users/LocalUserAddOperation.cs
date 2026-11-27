// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Async;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.HLE.Runtime.Services.Users;

internal sealed unsafe class LocalUserAddOperation
{
    private readonly HostRuntimeAsyncService _async;
    private readonly LocalUserDirectory _users;
    private readonly RuntimeServiceTrace _trace;
    private nint _context;
    private int _completed;

    internal LocalUserAddOperation(HostRuntimeAsyncService asyncService, LocalUserDirectory users,
        RuntimeServiceTrace trace)
    {
        _async = asyncService;
        _users = users;
        _trace = trace;
    }

    internal int Start(XAsyncBlock* block, nint identity)
    {
        _context = GCHandle.ToIntPtr(GCHandle.Alloc(this));

        try
        {
            var result = _async.Begin(block, _context, identity, null,
                (nint)(delegate* unmanaged[Stdcall]<XAsyncOperation, XAsyncProviderData*, int>)&Invoke);

            if (result < 0)
            {
                ReleaseContext();
            }

            return result;
        }
        catch
        {
            ReleaseContext();

            return RuntimeServiceResult.Failure;
        }
    }

    private void ReleaseContext()
    {
        var context = Interlocked.Exchange(ref _context, 0);

        if (context != 0)
        {
            GCHandle.FromIntPtr(context).Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(XAsyncOperation operation, XAsyncProviderData* data)
    {
        if (data == null || data->Context == 0)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        try
        {
            var instance = (LocalUserAddOperation)GCHandle.FromIntPtr(data->Context).Target!;

            return instance.Process(operation, data);
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    private int Process(XAsyncOperation operation, XAsyncProviderData* data)
    {
        switch (operation)
        {
            case XAsyncOperation.Begin:
                return _async.Schedule(data->Async, 0);

            case XAsyncOperation.DoWork:
                if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
                {
                    _users.AddUser();
                    _trace.Write("UserAddCompleted", $"block=0x{(nint)data->Async:X}; localId={_users.Profile.LocalId}; offline=True");
                    _async.Complete(data->Async, RuntimeServiceResult.Success, (nuint)sizeof(nint));
                }

                return RuntimeServiceResult.Success;

            case XAsyncOperation.GetResult:
                if (data->Buffer == 0 || data->BufferSize < (nuint)sizeof(nint))
                {
                    return RuntimeServiceResult.InsufficientBuffer;
                }

                var result = _users.Open(out var handle);

                *(nint*)data->Buffer = handle;

                return result;

            case XAsyncOperation.Cancel:
                if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
                {
                    _async.Complete(data->Async, RuntimeServiceResult.Aborted, 0);
                }

                return RuntimeServiceResult.Success;

            case XAsyncOperation.Cleanup:
                ReleaseContext();

                return RuntimeServiceResult.Success;

            default:
                return RuntimeServiceResult.NotImplemented;
        }
    }
}
