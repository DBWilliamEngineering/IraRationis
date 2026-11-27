// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PreviewWindowRect
{
    public int Left, Top, Right, Bottom;
}
