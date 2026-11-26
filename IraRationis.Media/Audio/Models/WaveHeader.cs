// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media.Audio.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct WaveHeader
{
    public nint Data;
    public uint Length;
    public uint Recorded;
    public nuint User;
    public uint Flags;
    public uint Loops;
    public nint Next;
    public nuint Reserved;
}
