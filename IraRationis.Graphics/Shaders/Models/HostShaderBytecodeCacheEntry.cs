// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Shaders.Models;

internal sealed class HostShaderBytecodeCacheEntry((string Source, string Profile) key, Func<byte[]?> compile, long charge)
{
    internal readonly (string Source, string Profile) Key = key;
    internal readonly Lazy<byte[]?> Value = new(compile, LazyThreadSafetyMode.ExecutionAndPublication);
    internal LinkedListNode<HostShaderBytecodeCacheEntry>? Node;
    internal byte[]? Result;
    internal long Charge = charge;
}
