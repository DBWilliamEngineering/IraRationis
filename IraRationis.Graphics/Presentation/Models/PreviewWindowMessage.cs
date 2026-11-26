// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PreviewWindowMessage
{
    public nint Window;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public int X, Y;
    public uint Private;
}
