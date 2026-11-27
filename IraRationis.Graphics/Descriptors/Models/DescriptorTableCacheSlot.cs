// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Descriptors.Models;

internal sealed class DescriptorTableCacheSlot
{
    internal DescriptorTableCacheLease? Active;
    internal ulong Fence;
    internal bool Ready;
    internal ComputeBufferTable Table;
}
