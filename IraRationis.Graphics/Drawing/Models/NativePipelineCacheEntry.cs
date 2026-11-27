// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Drawing.Models;

internal sealed unsafe class NativePipelineCacheEntry<TKey, TValue>
    where TKey : class
    where TValue : class
{
    internal readonly (nint Device, TKey Metadata) Key;
    internal readonly TValue Value;

    internal NativePipelineCacheEntry((nint Device, TKey Metadata) key, TValue value)
    {
        Key = key;

        Value = value;
    }
}
