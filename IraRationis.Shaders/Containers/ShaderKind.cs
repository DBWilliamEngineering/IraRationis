// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-shader/src/dxbc.rs and dxil.rs, Zettrox/ztrx.

namespace IraRationis.Shaders.Containers;

public enum ShaderKind : ushort
{
    Pixel,
    Vertex,
    Geometry,
    Hull,
    Domain,
    Compute,
    Library,
    RayGeneration,
    Intersection,
    AnyHit,
    ClosestHit,
    Miss,
    Callable,
    Mesh,
    Amplification,
    Node
}
