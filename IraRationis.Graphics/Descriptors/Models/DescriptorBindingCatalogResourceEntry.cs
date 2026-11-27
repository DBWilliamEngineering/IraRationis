// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Descriptors.Models;

internal sealed class DescriptorBindingCatalogResourceEntry(DescriptorResourceIdentity identity, ulong length, bool writable)
{
    internal readonly DescriptorResourceIdentity Identity = identity;
    internal readonly ulong Length = length;
    internal readonly bool Writable = writable;
    internal int Pins;
}
