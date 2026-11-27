// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Apu.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct XApuProcessCommand
{
    public XApuCommandId Id;
    public float PitchRamp;
    public float TargetPitch;
    public uint FirstFrame;
    public uint FrameCount;
    public uint InputLength;
    public nint Input;
    public uint OutputLength;
    public nint Output;
}
