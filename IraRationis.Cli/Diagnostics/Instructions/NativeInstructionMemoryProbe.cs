// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Loader.Instructions.Models;

namespace IraRationis.Cli.Diagnostics.Instructions;

internal sealed unsafe class NativeInstructionMemoryProbe
{
    private readonly delegate* unmanaged[Stdcall]<nuint, uint, WritableCacheLineProbeResult*, uint> _clear;

    internal NativeInstructionMemoryProbe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "irx-native.dll");
        var module = NativeLibrary.Load(path);

        if (!NativeLibrary.TryGetExport(module, "IrxProbeClearCacheLine", out var address))
        {
            throw new EntryPointNotFoundException(
                $"Native instruction probe is outdated: {path} does not export IrxProbeClearCacheLine. Rebuild and publish the managed and native components together.");
        }

        _clear = (delegate* unmanaged[Stdcall]<nuint, uint, WritableCacheLineProbeResult*, uint>)address;
    }

    internal WritableCacheLineProbeResult Clear(nuint address, bool residentPageQueries)
    {
        var result = new WritableCacheLineProbeResult { Size = (uint)sizeof(WritableCacheLineProbeResult) };
        var error = _clear(address, residentPageQueries ? 1U : 0U, &result);

        if (error != 0)
        {
            throw new Win32Exception((int)error, "Native cache-line probe rejected its arguments.");
        }

        return result;
    }
}
