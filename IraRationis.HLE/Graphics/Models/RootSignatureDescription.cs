// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx victory-gpu/src/root.rs serialization entry points.

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Graphics.Models;

[StructLayout(LayoutKind.Sequential)]
internal struct RootSignatureDescription
{
    public uint ParameterCount;
    public nint Parameters;
    public uint SamplerCount;
    public nint Samplers;
    public uint Flags;
}
