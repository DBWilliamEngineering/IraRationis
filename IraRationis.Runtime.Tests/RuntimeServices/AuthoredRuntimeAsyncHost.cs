// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services;
using IraRationis.HLE.Runtime.Services.Async.Models;
using IraRationis.Runtime.Tests.RuntimeServices.Models;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal sealed unsafe class AuthoredRuntimeAsyncHost : IDisposable
{
    private readonly Queue<AuthoredRuntimeAsyncOperation> _completions = [];
    private readonly Dictionary<nint, AuthoredRuntimeAsyncOperation> _operations = [];
    private readonly Queue<AuthoredRuntimeAsyncOperation> _work = [];
    private AuthoredRuntimeAsyncHeader* _header;
    private int _references = 1;

    internal AuthoredRuntimeAsyncHost()
    {
        var methods = (nint*)NativeMemory.AllocZeroed(19, (nuint)sizeof(nint));

        methods[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddReference;
        methods[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseReference;
        methods[7] = (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nint, byte*, nint, int>)&Begin;
        methods[9] = (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, uint, int>)&Schedule;
        methods[10] = (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int, nuint, void>)&Complete;
        methods[11] =
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint, nuint, nint, nuint*, int>)&GetResult;
        methods[18] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, int>)&SubmitCallback;

        _header = (AuthoredRuntimeAsyncHeader*)NativeMemory.Alloc((nuint)sizeof(AuthoredRuntimeAsyncHeader));
        _header->Methods = (nint)methods;
        _header->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(this));
    }

    internal nint Address
    {
        get
        {
            return (nint)_header;
        }
    }

    internal int BeginResult
    {
        get;
        set;
    }

    internal int ScheduleResult
    {
        get;
        set;
    }

    internal int BeginCalls
    {
        get;
        private set;
    }

    internal int WorkCalls
    {
        get;
        private set;
    }

    internal int CompleteCalls
    {
        get;
        private set;
    }

    internal int ResultCalls
    {
        get;
        private set;
    }

    internal int CancelCalls
    {
        get;
        private set;
    }

    internal int CleanupCalls
    {
        get;
        private set;
    }

    internal int CompletionCallbacks
    {
        get;
        private set;
    }

    internal int BoundaryFailures
    {
        get;
        private set;
    }

    internal nint LastIdentity
    {
        get;
        private set;
    }

    internal int LastCompletedResult
    {
        get;
        private set;
    }

    internal nuint LastCompletedSize
    {
        get;
        private set;
    }

    internal int QueuedWork
    {
        get
        {
            return _work.Count;
        }
    }

    internal int QueuedCompletions
    {
        get
        {
            return _completions.Count;
        }
    }

    public void Dispose()
    {
        if (_header == null)
        {
            return;
        }

        GCHandle.FromIntPtr(_header->Owner).Free();
        NativeMemory.Free((void*)_header->Methods);
        NativeMemory.Free(_header);
        _header = null;
    }

    internal bool DispatchWork()
    {
        if (!_work.TryDequeue(out var operation))
        {
            return false;
        }

        if (operation.Status != RuntimeServiceResult.Pending || operation.CleanupDelivered)
        {
            return true;
        }

        WorkCalls++;

        var result = InvokeProvider(operation, XAsyncOperation.DoWork);

        if (result != RuntimeServiceResult.Pending && operation.Status == RuntimeServiceResult.Pending)
        {
            CompleteOperation(operation, result, 0);
        }

        return true;
    }

    internal bool DispatchCompletion()
    {
        if (!_completions.TryDequeue(out var operation))
        {
            return false;
        }

        operation.CompletionDelivered = true;
        CompletionCallbacks++;

        ((delegate* unmanaged[Stdcall]<XAsyncBlock*, void>)operation.CompletionCallback)((XAsyncBlock*)operation.Block);

        return true;
    }

    internal void Cancel(XAsyncBlock* asyncBlock)
    {
        if (_operations.TryGetValue((nint)asyncBlock, out var operation) &&
            operation.Status == RuntimeServiceResult.Pending && !operation.CleanupDelivered)
        {
            CancelCalls++;

            InvokeProvider(operation, XAsyncOperation.Cancel);
        }
    }

    internal void CancelPending()
    {
        foreach (var operation in _operations.Values)
        {
            if (operation.Status == RuntimeServiceResult.Pending)
            {
                Cancel((XAsyncBlock*)operation.Block);
            }

            Cleanup(operation);
        }

        _work.Clear();
        _completions.Clear();
    }

    private static AuthoredRuntimeAsyncHost FromObject(nint self)
    {
        return (AuthoredRuntimeAsyncHost)GCHandle.FromIntPtr(((AuthoredRuntimeAsyncHeader*)self)->Owner).Target!;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddReference(nint self)
    {
        return (uint)Interlocked.Increment(ref FromObject(self)._references);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseReference(nint self)
    {
        return (uint)Interlocked.Decrement(ref FromObject(self)._references);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Begin(nint self, XAsyncBlock* block, nint context, nint identity, byte* identityName,
        nint provider)
    {
        var instance = FromObject(self);

        try
        {
            instance.BeginCalls++;

            return instance.BeginOperation(block, context, identity, provider);
        }
        catch
        {
            instance.BoundaryFailures++;

            return RuntimeServiceResult.Failure;
        }
    }

    private int BeginOperation(XAsyncBlock* block, nint context, nint identity, nint provider)
    {
        if (BeginResult < 0)
        {
            return BeginResult;
        }

        if (block == null || provider == 0 ||
            (_operations.TryGetValue((nint)block, out var previous) &&
             (!previous.CleanupDelivered || !previous.CompletionDelivered)))
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        var operation = new AuthoredRuntimeAsyncOperation
        {
            Block = (nint)block,
            Context = context,
            Identity = identity,
            Provider = provider,
            CompletionCallback = block->Callback,
            CompletionDelivered = block->Callback == 0
        };

        _operations[(nint)block] = operation;
        LastIdentity = identity;

        var result = InvokeProvider(operation, XAsyncOperation.Begin);

        if (result < 0)
        {
            CompleteOperation(operation, result, 0);
        }

        return RuntimeServiceResult.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Schedule(nint self, XAsyncBlock* block, uint delay)
    {
        var instance = FromObject(self);

        try
        {
            if (instance.ScheduleResult < 0)
            {
                return instance.ScheduleResult;
            }

            if (!instance._operations.TryGetValue((nint)block, out var operation) ||
                operation.Status != RuntimeServiceResult.Pending)
            {
                return RuntimeServiceResult.InvalidArgument;
            }

            instance._work.Enqueue(operation);

            return RuntimeServiceResult.Success;
        }
        catch
        {
            instance.BoundaryFailures++;

            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Complete(nint self, XAsyncBlock* block, int result, nuint size)
    {
        var instance = FromObject(self);

        try
        {
            if (instance._operations.TryGetValue((nint)block, out var operation))
            {
                instance.CompleteOperation(operation, result, size);
            }
        }
        catch
        {
            instance.BoundaryFailures++;
        }
    }

    private void CompleteOperation(AuthoredRuntimeAsyncOperation operation, int result, nuint size)
    {
        if (operation.Status != RuntimeServiceResult.Pending || result == RuntimeServiceResult.Pending)
        {
            return;
        }

        CompleteCalls++;
        LastCompletedResult = result;
        LastCompletedSize = size;
        operation.Status = result;
        operation.ResultSize = result < 0 ? 0 : size;

        if (result < 0 || size == 0)
        {
            Cleanup(operation);
        }

        if (operation.CompletionCallback != 0)
        {
            _completions.Enqueue(operation);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetResult(nint self, XAsyncBlock* block, nint identity, nuint capacity, nint buffer, nuint* used)
    {
        var instance = FromObject(self);

        try
        {
            return instance.TakeResult(block, identity, capacity, buffer, used);
        }
        catch
        {
            instance.BoundaryFailures++;

            return RuntimeServiceResult.Failure;
        }
    }

    private int TakeResult(XAsyncBlock* block, nint identity, nuint capacity, nint buffer, nuint* used)
    {
        if (!_operations.TryGetValue((nint)block, out var operation) || operation.Identity != identity
                                                                     || operation.ResultTaken)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        if (operation.Status != RuntimeServiceResult.Success)
        {
            return operation.Status;
        }

        if (buffer == 0 || capacity < operation.ResultSize)
        {
            return RuntimeServiceResult.InsufficientBuffer;
        }

        ResultCalls++;

        var result = InvokeProvider(operation, XAsyncOperation.GetResult, capacity, buffer);

        if (result >= 0 && used != null)
        {
            *used = operation.ResultSize;
        }

        operation.ResultTaken = true;
        Cleanup(operation);

        return result;
    }

    private void Cleanup(AuthoredRuntimeAsyncOperation operation)
    {
        if (operation.CleanupDelivered)
        {
            return;
        }

        operation.CleanupDelivered = true;
        CleanupCalls++;

        InvokeProvider(operation, XAsyncOperation.Cleanup);
    }

    private static int InvokeProvider(AuthoredRuntimeAsyncOperation operation, XAsyncOperation reason,
        nuint bufferSize = 0, nint buffer = 0)
    {
        var data = new XAsyncProviderData
        {
            Async = (XAsyncBlock*)operation.Block,
            Context = operation.Context,
            BufferSize = bufferSize,
            Buffer = buffer
        };

        return ((delegate* unmanaged[Stdcall]<XAsyncOperation, XAsyncProviderData*, int>)operation.Provider)(reason,
            &data);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SubmitCallback(nint self, nint queue, uint port, nint context, nint callback)
    {
        return RuntimeServiceResult.NotImplemented;
    }
}
