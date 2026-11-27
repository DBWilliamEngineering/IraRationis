// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Async.Models;

[StructLayout(LayoutKind.Sequential, Size = 56)]
internal struct XAsyncBlock
{
    public nint Queue;
    public nint Context;
    public nint Callback;
}
