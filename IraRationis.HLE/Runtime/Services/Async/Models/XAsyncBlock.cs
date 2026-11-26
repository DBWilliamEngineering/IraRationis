// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Async.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XAsyncBlock
{
    internal nint Queue;
    internal nint Context;
    internal nint Callback;
    private fixed byte _reserved[32];
}
