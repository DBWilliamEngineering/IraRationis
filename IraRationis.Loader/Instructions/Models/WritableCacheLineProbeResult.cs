// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Loader.Instructions.Models;

[StructLayout(LayoutKind.Sequential)]
public struct WritableCacheLineProbeResult
{
    public uint Size;
    public uint Cleared;
    public uint ResidentPageQueries;
    public uint ResidentPageHits;
    public uint RegionQueries;
    public uint AccessFaulted;
    public ulong ValidatedEnd;
}
