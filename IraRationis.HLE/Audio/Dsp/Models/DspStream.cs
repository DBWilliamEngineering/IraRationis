// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-hle/src/modules/dsp.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Dsp.Models;

[StructLayout(LayoutKind.Sequential)]
public struct DspStream
{
    public DspHandle* Owner;
    public uint Frames;
    public uint Channels;
    public DspStatus Status;
}
