// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Memory.Models;

namespace IraRationis.HLE.Memory;

internal sealed class MappedViewEntryComparer<T> : IComparer<MappedViewEntry<T>>
    where T : class
{
    public static readonly MappedViewEntryComparer<T> Instance = new();

    public int Compare(MappedViewEntry<T> left, MappedViewEntry<T> right)
    {
        return left.Address.CompareTo(right.Address);
    }
}
