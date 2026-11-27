// SPDX-License-Identifier: GPL-3.0-or-later
// Console resource ABI adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.

using System.Runtime.InteropServices;
using IraRationis.Graphics.Device.Models;

namespace IraRationis.Graphics.Resources.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct ResourceObject
{
    public GraphicsObjectHeader Header;
    public ResourceDescription Description;
    public GraphicsHeapProperties Properties;
    public uint HeapFlags;
    public ulong Address;
    public nint OwningHeap;
    public nint OwnedMemory;
    public long Generation;
    public nint FreeHook;
    public ulong AllocationAttributes;
    public nint HostResource;
    public ulong HostCharge;
}
