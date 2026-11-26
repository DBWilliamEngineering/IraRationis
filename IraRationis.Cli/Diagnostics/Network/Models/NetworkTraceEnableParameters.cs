// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Cli.Diagnostics.Network.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NetworkTraceEnableParameters
{
    public uint Version;
    public uint EnableProperty;
    public uint ControlFlags;
    public Guid SourceId;
    public NetworkTraceFilterDescriptor* Filters;
    public uint FilterCount;
}
