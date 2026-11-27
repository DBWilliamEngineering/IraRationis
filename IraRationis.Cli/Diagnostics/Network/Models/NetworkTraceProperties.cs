// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Cli.Diagnostics.Network.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NetworkTraceProperties
{
    public NetworkTraceHeader Header;
    public uint BufferSize;
    public uint MinimumBuffers;
    public uint MaximumBuffers;
    public uint MaximumFileSize;
    public uint LogFileMode;
    public uint FlushTimer;
    public uint EnableFlags;
    public int AgeLimit;
    public uint NumberOfBuffers;
    public uint FreeBuffers;
    public uint EventsLost;
    public uint BuffersWritten;
    public uint LogBuffersLost;
    public uint RealTimeBuffersLost;
    public nint LoggerThreadId;
    public uint LogFileNameOffset;
    public uint LoggerNameOffset;
}
