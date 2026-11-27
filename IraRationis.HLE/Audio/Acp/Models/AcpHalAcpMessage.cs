// SPDX-License-Identifier: GPL-3.0-or-later
// ACP object ABI and message layout cross-checked against VIctory/victory-fork audio.rs.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Audio.Acp.Models;

// 12-byte header, 16-byte payload; the command ID is unaligned.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AcpHalAcpMessage
{
    public uint Kind;
    public uint Dropped;
    public uint Microseconds;
    public uint Command;
    public ulong Id;
    public uint Frame;
}
