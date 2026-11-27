// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct VideoWindowMessage
{
    public nint Hwnd;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public int X, Y;
    public uint Private;
}
