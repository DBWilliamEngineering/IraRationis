// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal static unsafe class AuthoredHostRuntimeAsyncTargets
{
    internal const int IncomingSystemError = 421;
    internal const int IncomingPInvokeError = 527;
    internal const int OutgoingSystemError = 631;
    internal const int OutgoingPInvokeError = 739;
    internal const int FailureResult = unchecked((int)0x89245104);

    internal static int ReferenceCount;
    internal static int AddReferenceCalls;
    internal static int ReleaseReferenceCalls;
    internal static int TargetCalls;
    internal static int ObservedSystemError;
    internal static int ObservedPInvokeError;
    internal static nint Instance;
    internal static nint AsyncBlock;
    internal static nint Context;
    internal static nint Identity;
    internal static nint IdentityName;
    internal static nint ProviderCallback;
    internal static uint DelayMilliseconds;
    internal static int CompletionResult;
    internal static nuint BufferSize;
    internal static nint Buffer;
    internal static nint BufferUsed;
    internal static nint Queue;
    internal static uint Port;
    internal static nint QueueCallback;
    internal static int Result;
    internal static Action? DuringBegin;
    internal static bool CallbackFailed;
    internal static int ReferencesAfterCallback;

    internal static void Reset()
    {
        ReferenceCount = 1;
        AddReferenceCalls = 0;
        ReleaseReferenceCalls = 0;
        TargetCalls = 0;
        Result = 0;
        DuringBegin = null;
        CallbackFailed = false;
        ReferencesAfterCallback = 0;
    }

    internal static void SetIncomingErrors()
    {
        Marshal.SetLastPInvokeError(IncomingPInvokeError);
        Marshal.SetLastSystemError(IncomingSystemError);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static uint AddReference(nint instance)
    {
        Interlocked.Increment(ref AddReferenceCalls);
        var references = Interlocked.Increment(ref ReferenceCount);

        DisturbErrors();

        return (uint)references;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static uint ReleaseReference(nint instance)
    {
        Interlocked.Increment(ref ReleaseReferenceCalls);
        var references = Interlocked.Decrement(ref ReferenceCount);

        DisturbErrors();

        return (uint)references;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static int Begin(nint instance, XAsyncBlock* asyncBlock, nint context, nint identity, byte* identityName, nint providerCallback)
    {
        RecordCall(instance, asyncBlock);
        Context = context;
        Identity = identity;
        IdentityName = (nint)identityName;
        ProviderCallback = providerCallback;

        try
        {
            DuringBegin?.Invoke();
            ReferencesAfterCallback = ReferenceCount;
        }
        catch
        {
            CallbackFailed = true;
        }

        SetOutgoingErrors();

        return Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static int Schedule(nint instance, XAsyncBlock* asyncBlock, uint delayMilliseconds)
    {
        RecordCall(instance, asyncBlock);
        DelayMilliseconds = delayMilliseconds;
        SetOutgoingErrors();

        return Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static void Complete(nint instance, XAsyncBlock* asyncBlock, int result, nuint requiredBufferSize)
    {
        RecordCall(instance, asyncBlock);
        CompletionResult = result;
        BufferSize = requiredBufferSize;
        SetOutgoingErrors();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static int GetResult(nint instance, XAsyncBlock* asyncBlock, nint identity, nuint bufferSize, nint buffer, nuint* bufferUsed)
    {
        RecordCall(instance, asyncBlock);
        Identity = identity;
        BufferSize = bufferSize;
        Buffer = buffer;
        BufferUsed = (nint)bufferUsed;

        if (Result >= 0 && bufferUsed != null)
        {
            *bufferUsed = bufferSize;
        }

        SetOutgoingErrors();

        return Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    internal static int SubmitCallback(nint instance, nint queue, uint port, nint context, nint callback)
    {
        RecordCall(instance, null);
        Queue = queue;
        Port = port;
        Context = context;
        QueueCallback = callback;
        SetOutgoingErrors();

        return Result;
    }

    private static void RecordCall(nint instance, XAsyncBlock* asyncBlock)
    {
        ObservedSystemError = Marshal.GetLastSystemError();
        ObservedPInvokeError = Marshal.GetLastPInvokeError();
        Interlocked.Increment(ref TargetCalls);
        Instance = instance;
        AsyncBlock = (nint)asyncBlock;
    }

    private static void SetOutgoingErrors()
    {
        Marshal.SetLastPInvokeError(OutgoingPInvokeError);
        Marshal.SetLastSystemError(OutgoingSystemError);
    }

    private static void DisturbErrors()
    {
        Marshal.SetLastPInvokeError(811);
        Marshal.SetLastSystemError(919);
    }
}
