// SPDX-License-Identifier: GPL-3.0-or-later
// XDAT/XBCH/FSSM format adapted from DexNom/VIctory victory-shader/src/native.rs.

using IraRationis.Shaders.Native.Models;

namespace IraRationis.Shaders.Native;

public sealed record NativeProgram(
    NativeShaderStage Stage,
    bool Wave64,
    uint VectorRegisters,
    uint ScalarRegisters,
    uint LocalBytes,
    byte[] RootLayout,
    byte[] UsedResources,
    uint[]? Threads,
    byte[] Code)
{
    public byte[] StageMetadata
    {
        get;
        init;
    } = [];
}
