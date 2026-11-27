// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Runtime.Tests.RuntimeServices.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct AuthoredRuntimeAsyncHeader
{
    public nint Methods;
    public nint Owner;
}
