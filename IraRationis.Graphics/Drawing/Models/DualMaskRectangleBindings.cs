// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.Drawing.Models;

internal sealed record DualMaskRectangleBindings(
    TextureSource First, nint FirstHost, TextureSource Second, nint SecondHost, nint Heap, ulong GpuTable);
