// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct PreviewWindowClass
{
    public uint Size, Style;
    public nint Procedure;
    public int ClassExtra, WindowExtra;
    public nint Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
}
