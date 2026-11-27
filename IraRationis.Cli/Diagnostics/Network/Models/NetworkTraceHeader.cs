// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Cli.Diagnostics.Network.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct NetworkTraceHeader
{
    public uint BufferSize;
    public uint ProviderId;
    public ulong HistoricalContext;
    public long Timestamp;
    public Guid Guid;
    public uint ClientContext;
    public uint Flags;
}
