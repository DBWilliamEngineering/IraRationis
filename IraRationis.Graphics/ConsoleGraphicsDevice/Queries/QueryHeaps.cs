// SPDX-License-Identifier: GPL-3.0-or-later
// Console query heap ABI adapted from Zettrox/ztrx victory-gpu/src/query.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Queries.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly Guid QueryHeapId = new("0d9658ae-ed45-469e-a61d-970ec583cab4");
    private static nint _queryHeapTable;

    private static bool IsQueryHeap(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _queryHeapTable;
    }

    private static void RegisterQueryHeapMethods(nint* device)
    {
        if (sizeof(QueryHeapDescription) != 12 || sizeof(QueryHeapObject) != 32)
        {
            throw new InvalidOperationException("Unexpected query heap ABI layout.");
        }

        _queryHeapTable = (nint)ChildTable(24);

        device[39] =
            (nint)(delegate* unmanaged[Stdcall]<nint, QueryHeapDescription*, Guid*, nint*, int>)&CreateQueryHeap;
    }

    private static void ValidateQueryHeapRegistration(nint* device)
    {
        if (device[39]
            != (nint)(delegate* unmanaged[Stdcall]<nint, QueryHeapDescription*, Guid*, nint*, int>)&CreateQueryHeap)
        {
            throw new InvalidOperationException("ABI device slot 0x138 (CreateQueryHeap) is not registered correctly.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateQueryHeap(nint self, QueryHeapDescription* description, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null)
        {
            return InvalidArgument;
        }

        if (iid == null || (*iid != QueryHeapId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId
                            && *iid != PageableId))
        {
            return NoInterface;
        }

        if (output == null)
        {
            return 1;
        }

        QueryHeapObject* heap = null;

        try
        {
            heap = (QueryHeapObject*)NativeMemory.AllocZeroed((nuint)sizeof(QueryHeapObject));

            heap->Header.Table = _queryHeapTable;

            heap->Header.References = 1;

            heap->Description = *description;

            LiveObjects[(nint)heap] = _queryHeapTable;
        }
        catch (OutOfMemoryException)
        {
            if (heap != null)
            {
                NativeMemory.Free(heap);
            }

            return OutOfMemory;
        }

        *output = (nint)heap;

        Console.WriteLine(
            $"[graphics] Query heap: type={heap->Description.Kind}, count={heap->Description.Count}, nodes=0x{heap->Description.Nodes:X}, object=0x{(nuint)heap:X} (CPU metadata only).");

        return 0;
    }

    private static void ReleaseQueryHeap(nint value)
    {
        _ = LiveObjects.TryRemove(value, out _);

        foreach (var key in ResourcePrivateData.Keys)
        {
            if (key.Item1 == value)
            {
                _ = ResourcePrivateData.TryRemove(key, out _);
            }
        }

        NativeMemory.Free((void*)value);
    }
}
