// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Services.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct RuntimeServiceObjectHeader
{
    internal nint Table;
    internal nint Owner;
}
