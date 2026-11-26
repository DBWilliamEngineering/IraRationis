// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Textures;
using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Graphics;

public static class XgModule
{
    public static HleModule Create()
    {
        return new HleModule("xg", ["xg_xs.dll"],
        [
            new HleExport("XGSuggestSwizzleMode", (nuint)TextureComputer.SuggestAddress),
            new HleExport("XGCreateTextureComputer", (nuint)TextureComputer.CreateAddress)
        ]);
    }
}
