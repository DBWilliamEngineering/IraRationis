// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Media.Audio.Models;

[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct WindowsPcmClockMultimediaTime
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(4)] public uint Value;
}
