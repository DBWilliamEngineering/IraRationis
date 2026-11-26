// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Drawing.Models;

internal sealed unsafe record VertexInputElement(
    string Name,
    uint Index,
    uint Format,
    uint Slot,
    uint Offset,
    uint Class,
    uint Step);
