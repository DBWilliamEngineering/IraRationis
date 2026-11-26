// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-hle/src/modules/dsp.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Dsp.Models;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct DspStatus
{
    public uint Submitted;
    public uint Completed;
    public uint Reserved;
    public int Result;
}
