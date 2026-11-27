// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/runtime.rs, use_console_timers.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Kernel.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct ConsoleTimersPowerThrottlingState
{
    public uint Version;
    public uint ControlMask;
    public uint StateMask;
}
