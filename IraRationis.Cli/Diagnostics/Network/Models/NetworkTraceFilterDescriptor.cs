// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Cli.Diagnostics.Network.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NetworkTraceFilterDescriptor
{
    public ulong Pointer;
    public uint Size;
    public uint Type;
}
