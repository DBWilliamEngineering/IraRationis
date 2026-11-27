// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal static unsafe class AuthoredLocalUserCallbacks
{
    internal static int Completions;
    internal static int Changes;
    internal static nint LastBlock;

    internal static nint CompletionAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<XAsyncBlock*, void>)&Completed;
        }
    }

    internal static nint ChangeAddress
    {
        get
        {
            return (nint)(delegate* unmanaged[Stdcall]<nint, ulong, uint, void>)&Changed;
        }
    }

    internal static void Reset()
    {
        Completions = 0;
        Changes = 0;
        LastBlock = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Completed(XAsyncBlock* block)
    {
        Completions++;
        LastBlock = (nint)block;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Changed(nint context, ulong localId, uint change)
    {
        Changes++;
    }
}
