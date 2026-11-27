// SPDX-License-Identifier: GPL-3.0-or-later
// Wired DualSense input provider. No virtual device, output reports or third-party driver.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Input.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct GameInputRawDevice
{
    public nint Handle;
    public uint Type;
}
