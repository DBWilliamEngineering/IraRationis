// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VideoWindowClass
{
    public uint Style;
    public nint Procedure;
    public int ClassExtra, WindowExtra;
    public nint Instance, Icon, Cursor, Background, MenuName, ClassName;
}
