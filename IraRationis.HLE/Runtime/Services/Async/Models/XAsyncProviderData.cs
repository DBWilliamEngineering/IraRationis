// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Async.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XAsyncProviderData
{
    internal XAsyncBlock* Async;
    internal nuint BufferSize;
    internal nint Buffer;
    internal nint Context;
}
