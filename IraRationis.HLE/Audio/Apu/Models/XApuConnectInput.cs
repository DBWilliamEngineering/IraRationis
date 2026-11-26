// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Apu.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XApuConnectInput
{
    public uint Processing;
    public uint Streams;
    public uint CommandsPerStream;
    public uint Memory;
    public uint FramesPerOutput;
    public uint Options;
}
