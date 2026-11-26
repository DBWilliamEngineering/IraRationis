// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VideoWindowRect
{
    public int Left, Top, Right, Bottom;
}
