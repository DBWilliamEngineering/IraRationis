// SPDX-License-Identifier: GPL-3.0-or-later
// Guest PresentX ABI prefix and old/new queue slots cross-checked against
// LooperSalty/VIctory and fxceless/victory-fork, crates/victory-gpu/src/queue.rs.

using System.Runtime.InteropServices;

namespace IraRationis.Graphics.Presentation.Models;

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PresentPlanePrefix
{
    [FieldOffset(0)] public ulong Token;
    [FieldOffset(8)] public uint ResourceCount;
    [FieldOffset(16)] public nint Resources;
}
