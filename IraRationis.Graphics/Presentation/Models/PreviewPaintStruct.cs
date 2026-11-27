// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PreviewPaintStruct
{
    public nint Dc;
    public int Erase;
    public PreviewWindowRect Paint;
    public int Restore, IncrementalUpdate;
    public fixed byte Reserved[32];
}
