// SPDX-License-Identifier: GPL-3.0-or-later
// ABI adapted from DexNom/VIctory crates/victory-gpu/src/list.rs and object.rs.

using IraRationis.Graphics.Commands.Models;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics.Commands;

internal sealed class RecordedCommandState
{
    public readonly RecordedRootState Compute = new();
    public readonly RecordedRootState Graphics = new();
    public readonly List<nint> HostViews = new();
    public readonly List<MemoryBoundary> MemoryBoundaries = new();
    public readonly HashSet<nint> Owned = new();
    public readonly List<nint> Uploads = new();
    public readonly Dictionary<uint, RecordedBufferView> Vertices = new();
    public float[]? Blend;
    public bool BlendSet;
    public ulong? Depth;
    public (float Min, float Max)? DepthBounds;
    public nint[] Heaps = [];
    public int HostCommandCount;
    public RecordedBufferView? Index;
    public string? LastHostCommand;
    public nint Pipeline;
    public RECT[]? Scissors;
    public uint? Stencil;
    public ulong[] Targets = [];
    public uint? Topology;
    public ulong UploadBytes;
    public D3D12_VIEWPORT[]? Viewports;

    public void RetainObject(nint value)
    {
        if (value != 0 && Owned.Add(value))
        {
            _ = ConsoleGraphicsDevice.Retain(value);
        }
    }
}
