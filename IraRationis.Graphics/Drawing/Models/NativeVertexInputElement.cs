// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Drawing.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeVertexInputElement
{
    public nint Name;
    public uint Index;
    public uint Format;
    public uint Slot;
    public uint Offset;
    public uint Class;
    public uint Step;
}
