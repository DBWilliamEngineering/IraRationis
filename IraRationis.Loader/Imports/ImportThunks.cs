// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/thunks.rs.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Loader.Imports.Models;
using IraRationis.Loader.Memory;

namespace IraRationis.Loader.Imports;

internal sealed unsafe class ImportThunks
{
    private const int ThunkSize = 32;
    private readonly nint _address;
    private readonly nuint _size;
    private int _used;

    internal ImportThunks(int count)
    {
        _size = (nuint)checked((Math.Max(1, count) * ThunkSize + 4095) & ~4095);

        _address = ExecutableMemory.Allocate(_size);
    }

    internal nuint Emit(MissingImport import, UnimplementedPolicy policy)
    {
        if ((nuint)(_used + ThunkSize) > _size)
        {
            throw new InvalidOperationException("Import thunk block is full.");
        }

        // Handles and executable blocks intentionally live until process exit.
        var handle = GCHandle.Alloc(new ImportThunksSite(import, policy));
        var code = new Span<byte>((void*)(_address + _used), ThunkSize);

        code.Fill(0xCC);

        code[0] = 0x48;

        code[1] = 0xB9;

        BinaryPrimitives.WriteUInt64LittleEndian(code[2..], (nuint)GCHandle.ToIntPtr(handle));

        code[10] = 0x48;

        code[11] = 0xB8;

        BinaryPrimitives.WriteUInt64LittleEndian(code[12..],
            (nuint)(delegate* unmanaged[Stdcall]<nint, ulong>)&OnMissing);

        code[20] = 0xFF;

        code[21] = 0xE0;

        var address = (nuint)(_address + _used);

        _used += ThunkSize;

        return address;
    }

    internal void Seal()
    {
        ExecutableMemory.Seal(_address, _size);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static ulong OnMissing(nint handle)
    {
        try
        {
            var site = (ImportThunksSite)GCHandle.FromIntPtr(handle).Target!;
            var calls = Interlocked.Increment(ref site.Calls);
            var message = $"Unimplemented import: {site.Import.Dll}!{site.Import.Symbol}";

            if (site.Policy == UnimplementedPolicy.Trap)
            {
                Console.Error.WriteLine(message);

                Environment.FailFast(message);
            }

            if (calls == 1)
            {
                Console.Error.WriteLine($"{message}; returning zero (unsafe bring-up mode).");
            }
        }
        catch (Exception exception)
        {
            Environment.FailFast("Import thunk failed.", exception);
        }

        return 0;
    }
}
