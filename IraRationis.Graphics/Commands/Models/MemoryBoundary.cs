// SPDX-License-Identifier: GPL-3.0-or-later
// ABI and comparison conventions adapted from DexNom/VIctory victory-gpu/src/list.rs.

using IraRationis.Graphics.Drawing.Models;
using IraRationis.Graphics.Shaders.Models;
using IraRationis.Graphics.Textures.Models;

namespace IraRationis.Graphics.Commands.Models;

internal readonly unsafe record struct MemoryBoundary(
    nint Host,
    MemoryWrite? Write,
    MemoryWait? Wait,
    DeferredCopy? Copy,
    VertexFetchDrawBoundary? Draw,
    RectangleDrawBoundary? Rectangle,
    TexturedDrawBoundary? Textured,
    ColorLutComputeBoundary? Compute,
    PlanarColorDrawBoundary? PlanarColor = null,
    IndexedUiBoundary? IndexedUi = null);
