// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.Shaders.Models;

internal sealed unsafe record ColorLutComputeBoundary(
    TextureSource Input,
    TextureSource Output,
    ulong Table,
    nint InputHost,
    nint OutputHost,
    nint Root,
    nint Pipeline);
