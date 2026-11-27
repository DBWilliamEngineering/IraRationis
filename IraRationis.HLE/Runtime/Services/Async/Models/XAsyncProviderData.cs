// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Async.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XAsyncProviderData
{
    public XAsyncBlock* Async;
    public nuint BufferSize;
    public nint Buffer;
    public nint Context;
}
