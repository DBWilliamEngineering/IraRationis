// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Diagnostics.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HostDebugMessage
{
    public int Category;
    public int Severity;
    public int Id;
    public nint Description;
    public nuint DescriptionBytes;
}
