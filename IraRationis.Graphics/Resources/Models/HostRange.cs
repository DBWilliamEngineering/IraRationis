// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostRange
{
    public nuint Begin;
    public nuint End;
}
