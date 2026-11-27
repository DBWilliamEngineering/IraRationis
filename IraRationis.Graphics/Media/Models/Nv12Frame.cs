// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Media.Models;

internal sealed class Nv12Frame
{
    public nint CopyAllocator, CopyList;
    public ulong FenceValue;
    public nint Texture, Upload, Output, Heap, Buffer, Allocator, List;
    public nint UploadAddress;
    public bool UploadMapped;
}
