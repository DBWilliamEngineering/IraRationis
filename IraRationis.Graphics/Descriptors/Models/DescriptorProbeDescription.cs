// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Descriptors.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct DescriptorProbeDescription
{
    public DescriptorHeapDescription Description;
    public DescriptorPlacement Placement;
}
