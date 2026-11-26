// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Apu.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct XApuResult
{
    public XApuCommandId Id;
    public uint State;
    public ulong StateData;
    public ulong Microseconds;
    public int Error;
    public uint Frames;
    public uint OutputLength;
    public nint Output;
    public float Pitch;
    public uint InputLength;
    public nint Input;
}
