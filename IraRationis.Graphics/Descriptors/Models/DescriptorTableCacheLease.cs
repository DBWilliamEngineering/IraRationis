// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Descriptors.Models;

internal sealed class DescriptorTableCacheLease(int slotIndex, ComputeBufferTable table, bool requiresMaterialization)
{
    internal int SlotIndex
    {
        get;
    } = slotIndex;

    internal ComputeBufferTable Table
    {
        get;
    } = table;

    internal bool RequiresMaterialization
    {
        get;
    } = requiresMaterialization;

    internal int DescriptorOffset
    {
        get
        {
            return SlotIndex * DescriptorTableCache.DescriptorsPerTable;
        }
    }
}
