// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Textures.Models;

internal sealed unsafe record TextureSource(
    ulong Handle,
    ulong Token,
    uint Kind,
    nint Resource,
    long Generation,
    byte[]? Description);
