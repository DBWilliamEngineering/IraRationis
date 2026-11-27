// SPDX-License-Identifier: GPL-3.0-or-later
// Console DXGI object layouts derived from Zettrox/ztrx victory-gpu/src/dxgi.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdapterDescription
{
    public fixed char Name[128];
    public uint Vendor;
    public uint Device;
    public uint Subsystem;
    public uint Revision;
    public nuint DedicatedVideoMemory;
    public nuint DedicatedSystemMemory;
    public nuint SharedSystemMemory;
    public uint LuidLow;
    public int LuidHigh;
}
