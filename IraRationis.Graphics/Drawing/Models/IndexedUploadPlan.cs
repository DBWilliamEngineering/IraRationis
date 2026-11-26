// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Drawing.Models;

// The vertex view is kept at its original origin so signed base-vertex
// arithmetic and SV_VertexID remain correct. Only the selected index range
// is staged. The caller must establish CPU authority before uploading.
internal sealed record IndexedUploadPlan(
    ulong IndexAddress,
    uint IndexFormat,
    int VertexBytes,
    uint VertexStride,
    int IndexOffset,
    int IndexBytes,
    int Capacity,
    int BaseVertex);
