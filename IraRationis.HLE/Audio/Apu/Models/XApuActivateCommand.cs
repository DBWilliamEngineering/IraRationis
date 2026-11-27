// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Apu.Models;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct XApuActivateCommand
{
    public XApuCommandId Id;
    public uint Channels;
    public uint InitLength;
    public nint Init;
    public float StartPitch;
    public uint ProcessingLength;
    public nint Processing;
}
