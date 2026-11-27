// SPDX-License-Identifier: GPL-3.0-or-later
// xapu.dll ABI cross-checked against VIctory/victory-fork modules/apu.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Apu.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct XApuConnectOutput
{
    public nint Memory;
    public nint Event;
}
