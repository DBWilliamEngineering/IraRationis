// SPDX-License-Identifier: GPL-3.0-or-later
// ABI adapted from DexNom/VIctory crates/victory-gpu/src/list.rs and object.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Commands.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RecordedBufferView
{
    public ulong Address;
    public uint Size;
    public uint FormatOrStride;
}
