// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Drawing.Models;

internal readonly record struct VertexFetchDrawRange(
    bool Indexed,
    uint Count,
    uint Instances,
    uint First,
    int BaseVertex,
    uint FirstInstance);
