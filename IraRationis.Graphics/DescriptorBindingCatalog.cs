// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

internal enum BufferViewKind
{
    Constant,
    ShaderResource,
    UnorderedAccess
}

internal readonly record struct DescriptorResourceIdentity(ulong Id, ulong Generation);

internal readonly record struct BufferDescriptor(
    DescriptorResourceIdentity Resource,
    BufferViewKind Kind,
    ulong Offset,
    ulong Length);

internal readonly record struct ComputeBufferTable(
    BufferDescriptor Constant,
    BufferDescriptor Input,
    BufferDescriptor Output);

/// <summary>
///     Owner-thread catalog for explicitly registered host buffers. Ids are logical
///     identities, not native addresses. A new registration cannot revive a stale view.
///     Native object ownership remains with the caller until all referencing fences finish.
/// </summary>
internal sealed class DescriptorBindingCatalog
{
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ulong, ResourceEntry> _resources = [];
    private readonly int _capacity;
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

        _resources.Add(id, new ResourceEntry(identity, length, writable));

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

    private ResourceEntry Resolve(DescriptorResourceIdentity identity)
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

    private sealed class ResourceEntry(DescriptorResourceIdentity identity, ulong length, bool writable)
    {
        internal readonly DescriptorResourceIdentity Identity = identity;
        internal readonly ulong Length = length;
        internal readonly bool Writable = writable;
        internal int Pins;
    }
}
