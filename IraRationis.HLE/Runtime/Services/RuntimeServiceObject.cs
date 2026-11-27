// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Models;

namespace IraRationis.HLE.Runtime.Services;

internal sealed unsafe class RuntimeServiceObject
{
    internal static readonly Guid UnknownInterface = new("00000000-0000-0000-c000-000000000046");
    private readonly HashSet<Guid> _interfaces;
    private readonly object _owner;
    private readonly Func<Guid, nint>? _resolveInterface;
    private int _references = 1;

    internal RuntimeServiceObject(object owner, ReadOnlySpan<nint> methods, params Guid[] interfaces)
        : this(owner, methods, null, interfaces)
    {
    }

    internal RuntimeServiceObject(object owner, ReadOnlySpan<nint> methods, Func<Guid, nint>? resolveInterface,
        params Guid[] interfaces)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (methods.Length < 3 || methods[3..].Contains((nint)0))
        {
            throw new ArgumentException("Every service method must have an explicit ABI target.", nameof(methods));
        }

        _owner = owner;
        _resolveInterface = resolveInterface;
        _interfaces = [UnknownInterface, .. interfaces];

        var table = (nint*)NativeMemory.Alloc((nuint)(methods.Length * sizeof(nint)));
        var header = (RuntimeServiceObjectHeader*)NativeMemory.Alloc((nuint)sizeof(RuntimeServiceObjectHeader));

        methods.CopyTo(new Span<nint>(table, methods.Length));

        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddReference;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseReference;

        header->Table = (nint)table;
        header->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(this));

        Address = (nint)header;
    }

    internal nint Address { get; }

    internal nint Retain()
    {
        Interlocked.Increment(ref _references);

        return Address;
    }

    internal static T Owner<T>(nint address) where T : class
    {
        return (T)FromAddress(address)._owner;
    }

    private static RuntimeServiceObject FromAddress(nint address)
    {
        return (RuntimeServiceObject)GCHandle.FromIntPtr(((RuntimeServiceObjectHeader*)address)->Owner).Target!;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(nint self, Guid* requested, nint* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        if (requested == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        try
        {
            var instance = FromAddress(self);

            if (instance._resolveInterface is not null)
            {
                *output = instance._resolveInterface(*requested);

                return *output != 0 ? RuntimeServiceResult.Success : RuntimeServiceResult.NoInterface;
            }

            if (!instance._interfaces.Contains(*requested))
            {
                return RuntimeServiceResult.NoInterface;
            }

            *output = instance.Retain();

            return RuntimeServiceResult.Success;
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddReference(nint self)
    {
        return (uint)Interlocked.Increment(ref FromAddress(self)._references);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseReference(nint self)
    {
        var instance = FromAddress(self);

        // One process-lifetime reference keeps cached guest service pointers and queued callbacks valid.
        for (;;)
        {
            var references = Volatile.Read(ref instance._references);

            if (references <= 1)
            {
                return 1;
            }

            if (Interlocked.CompareExchange(ref instance._references, references - 1, references) == references)
            {
                return (uint)(references - 1);
            }
        }
    }
}
