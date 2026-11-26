// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-hle/src/modules/dsp.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Dsp.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct DspStreamDescription
{
    public uint Kind;
    public uint Options;
    public uint Frames;
    public uint Channels;
    public uint ImpulseResponseFloats;
    public float* ImpulseResponse;
}
