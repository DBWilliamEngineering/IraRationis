// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Apu.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct XApuCommandId
{
    public uint Kind;
    public uint Stream;
    public uint Sequence;
    public uint Options;
    public ulong Context;
}
