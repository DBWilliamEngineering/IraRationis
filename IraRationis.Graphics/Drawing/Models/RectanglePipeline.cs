// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Drawing.Models;

internal sealed unsafe record RectanglePipeline(
    nint Root,
    nint Pipeline,
    VertexInputElement[] Elements,
    bool IndexedArray = false,
    bool DualMask = false,
    int IndexedVertexConstantBytes = 64);
