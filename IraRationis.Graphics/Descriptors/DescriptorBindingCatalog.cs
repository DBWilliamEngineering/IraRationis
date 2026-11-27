// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Descriptors.Models;

namespace IraRationis.Graphics.Descriptors;

/// <summary>
///     Owner-thread catalog for explicitly registered host buffers. Ids are logical
///     identities, not native addresses. A new registration cannot revive a stale view.
///     Native object ownership remains with the caller until all referencing fences finish.
/// </summary>
internal sealed class DescriptorBindingCatalog
{
    private readonly int _capacity;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ulong, DescriptorBindingCatalogResourceEntry> _resources = [];
    private ulong _generation;

    internal DescriptorBindingCatalog(int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);

        _capacity = capacity;
    }

    internal DescriptorResourceIdentity Register(ulong id, ulong length, bool writable)
    {
        CheckOwner();

        if (id == 0 || length == 0 || _resources.ContainsKey(id))
        {
            throw new ArgumentException("Expected a unique nonzero buffer identity and length.");
        }

        if (_resources.Count == _capacity || _generation == ulong.MaxValue)
        {
            throw new InvalidOperationException("Descriptor resource catalog capacity or generation exhausted.");
        }

        var identity = new DescriptorResourceIdentity(id, ++_generation);

        _resources.Add(id, new DescriptorBindingCatalogResourceEntry(identity, length, writable));

        return identity;
    }

    internal void Remove(DescriptorResourceIdentity identity)
    {
        CheckOwner();

        var entry = Resolve(identity);

        if (entry.Pins != 0)
        {
            throw new InvalidOperationException("Cannot remove a resource referenced by an active or in-flight table.");
        }

        _resources.Remove(identity.Id);
    }

    internal void Validate(ComputeBufferTable table)
    {
        CheckOwner();

        ValidateView(table.Constant, BufferViewKind.Constant);
        ValidateView(table.Input, BufferViewKind.ShaderResource);
        ValidateView(table.Output, BufferViewKind.UnorderedAccess);

        // This first contract excludes simultaneous read and write views of the same
        // resource, even for nonoverlapping ranges. It does not model alias heaps.
        if (table.Output.Resource == table.Constant.Resource || table.Output.Resource == table.Input.Resource)
        {
            throw new InvalidOperationException("A writable buffer cannot also be bound for reading in this table.");
        }
    }

    internal void Pin(ComputeBufferTable table)
    {
        Validate(table);

        var constant = Resolve(table.Constant.Resource);
        var input = Resolve(table.Input.Resource);
        var output = Resolve(table.Output.Resource);

        if (constant.Pins == int.MaxValue || input.Pins == int.MaxValue || output.Pins == int.MaxValue)
        {
            throw new InvalidOperationException("Descriptor resource pin count exhausted.");
        }

        constant.Pins++;

        if (!ReferenceEquals(constant, input))
        {
            input.Pins++;
        }

        output.Pins++;
    }

    internal void Unpin(ComputeBufferTable table)
    {
        CheckOwner();

        var constant = Resolve(table.Constant.Resource);
        var input = Resolve(table.Input.Resource);
        var output = Resolve(table.Output.Resource);

        if (constant.Pins <= 0 || input.Pins <= 0 || output.Pins <= 0)
        {
            throw new InvalidOperationException("Descriptor resource pin count underflow.");
        }

        constant.Pins--;

        if (!ReferenceEquals(constant, input))
        {
            input.Pins--;
        }

        output.Pins--;
    }

    private void ValidateView(BufferDescriptor view, BufferViewKind expectedKind)
    {
        var resource = Resolve(view.Resource);

        if (view.Kind != expectedKind || view.Length == 0 || view.Offset > resource.Length ||
            view.Length > resource.Length - view.Offset)
        {
            throw new ArgumentException("Descriptor kind or buffer range is invalid.");
        }

        if (view.Kind == BufferViewKind.Constant)
        {
            if ((view.Offset & 255UL) != 0 || (view.Length & 255UL) != 0 || view.Length > 65536)
            {
                throw new ArgumentException("CBV requires a 256-byte aligned range of at most 64 KiB.");
            }

            return;
        }

        // This path is specifically for R32_UINT typed views, not raw or structured buffers.
        if ((view.Offset & 3UL) != 0 || (view.Length & 3UL) != 0 || view.Length / 4 > uint.MaxValue)
        {
            throw new ArgumentException("R32_UINT descriptors require aligned, representable element ranges.");
        }

        if (view.Kind == BufferViewKind.UnorderedAccess && !resource.Writable)
        {
            throw new InvalidOperationException("UAV requires a resource registered for unordered access.");
        }
    }

    private DescriptorBindingCatalogResourceEntry Resolve(DescriptorResourceIdentity identity)
    {
        if (!_resources.TryGetValue(identity.Id, out var entry) || entry.Identity != identity)
        {
            throw new InvalidOperationException("Descriptor references an unknown or stale resource generation.");
        }

        return entry;
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException("Descriptor catalog is owned by its creating thread.");
        }
    }
}
