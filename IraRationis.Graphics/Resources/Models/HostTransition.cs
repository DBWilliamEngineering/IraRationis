// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct HostTransition
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(4)] public uint Flags;
    [FieldOffset(8)] public nint Resource;
    [FieldOffset(16)] public uint Subresource;
    [FieldOffset(20)] public uint Before;
    [FieldOffset(24)] public uint After;
}
