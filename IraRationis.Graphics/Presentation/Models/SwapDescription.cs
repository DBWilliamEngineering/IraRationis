// SPDX-License-Identifier: GPL-3.0-or-later
// Presentation follows the title-window / DXGI flip-model architecture in
// DexNom/VIctory, victory-gpu/src/host.rs. This implementation uses an
// exact-format copy on the producing queue, not a CPU readback or plane shader.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct SwapDescription
{
    public uint Width, Height, Format;
    public int Stereo;
    public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
}
