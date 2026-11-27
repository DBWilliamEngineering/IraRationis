// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Diagnostics.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct ProbeQueueDescription
{
    public uint Kind;
    public int Priority;
    public uint Flags;
    public uint Nodes;
}
