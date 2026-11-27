// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Async.Models;
using IraRationis.HLE.Runtime.Services.Users.Models;

namespace IraRationis.HLE.Runtime.Services.Users.LocalUserService;

internal sealed unsafe partial class LocalUserService
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AddAsync(nint self, uint options, XAsyncBlock* block)
    {
        if (block == null || (options & ~7U) != 0 || (options & 5) == 5 || (options & 3) == 3)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        try
        {
            var instance = FromObject(self);

            instance._trace.Write("UserAddBegin", $"options=0x{options:X}; block=0x{(nint)block:X}; queue=0x{block->Queue:X}; offline=True");

            var operation = new LocalUserAddOperation(instance._async, instance._users, instance._trace);

            return operation.Start(block, instance._baseObject.Address);
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AddResult(nint self, XAsyncBlock* block, nint* output)
    {
        if (output == null || block == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        try
        {
            var instance = FromObject(self);
            var result = instance._async.GetResult(block, instance._baseObject.Address, (nuint)sizeof(nint),
                (nint)output, null);

            instance._trace.Write("UserAddResult", $"block=0x{(nint)block:X}; hresult=0x{unchecked((uint)result):X8}; handle=0x{*output:X}");

            return result;
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RegisterChange(nint self, nint queue, nint context, nint callback, ulong* token)
    {
        if (token == null || callback == 0)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *token = 0;

        try
        {
            var instance = FromObject(self);

            lock (instance._registrationGate)
            {
                if (instance._registrations.Count >= 256 || instance._nextRegistration == ulong.MaxValue)
                {
                    return RuntimeServiceResult.OutOfMemory;
                }

                var registration = new LocalUserChangeRegistration(queue, context, callback);
                var nextToken = ++instance._nextRegistration;

                instance._registrations.Add(nextToken, registration);

                *token = nextToken;
            }

            // Initial Add completion is not a SignedInAgain event. This local profile is immutable.
            //TODO: Damon -> Add queued notifications and unregister draining before introducing profile changes or sign-out.
            instance._trace.Write("UserChangeRegistered", $"token={*token}; queue=0x{queue:X}; mutableProfile=False");

            return RuntimeServiceResult.Success;
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte UnregisterChange(nint self, ulong token, byte wait)
    {
        var instance = FromObject(self);

        lock (instance._registrationGate)
        {
            return instance._registrations.Remove(token) ? (byte)1 : (byte)0;
        }
    }
}
