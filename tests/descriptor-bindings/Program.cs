// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Descriptors;
using IraRationis.Graphics.Descriptors.Models;

namespace IraRationis.DescriptorBindings.Contracts;

internal static class Program
{
    private static int Main()
    {
        TestCatalog();
        TestCache();
        TestCancellation();

        Console.WriteLine("IRX-R52 CPU contracts passed.");

        return 0;
    }

    private static void TestCatalog()
    {
        var catalog = new DescriptorBindingCatalog(2);
        var table = CreateTable(catalog);

        catalog.Validate(table);

        Reject<ArgumentException>(() => catalog.Validate(table with
        {
            Constant = table.Constant with { Offset = 4 }
        }), "Misaligned CBV");

        Reject<ArgumentException>(() => catalog.Validate(table with
        {
            Input = table.Input with { Length = 0 }
        }), "Empty SRV");

        Reject<ArgumentException>(() => catalog.Validate(table with
        {
            Input = table.Input with { Offset = ulong.MaxValue }
        }), "Overflowing range");

        Reject<ArgumentException>(() => catalog.Validate(table with
        {
            Input = table.Input with { Offset = 1020, Length = 8 }
        }), "Out-of-bounds range");

        Reject<InvalidOperationException>(() => catalog.Validate(table with
        {
            Output = table.Output with { Resource = table.Input.Resource }
        }), "Read-only resource cannot be UAV");

        Reject<InvalidOperationException>(() => catalog.Validate(table with
        {
            Input = table.Input with { Resource = table.Output.Resource, Offset = 0 }
        }), "Simultaneous read and write alias");

        Reject<InvalidOperationException>(() => catalog.Register(3, 4, true), "Bounded registry");

        catalog.Remove(table.Input.Resource);

        var replacement = catalog.Register(table.Input.Resource.Id, 1024, false);

        Require(replacement != table.Input.Resource, "Resource generation changed");

        Reject<InvalidOperationException>(() => catalog.Validate(table), "Stale generation refused");

        catalog.Validate(table with
        {
            Constant = table.Constant with { Resource = replacement },
            Input = table.Input with { Resource = replacement }
        });
    }

    private static void TestCache()
    {
        var catalog = new DescriptorBindingCatalog(2);
        var first = CreateTable(catalog);
        var second = first with { Constant = first.Constant with { Offset = 256 } };
        var third = first with { Input = first.Input with { Offset = 768 } };
        var cache = new DescriptorTableCache(catalog, 2);

        var one = Acquire(cache, first);

        Require(one.RequiresMaterialization, "First table miss");

        Reject<InvalidOperationException>(() => catalog.Remove(first.Input.Resource), "Active resource pinned");

        Reject<InvalidOperationException>(() => cache.MarkSubmitted(one, 1), "Unmaterialized table cannot submit");

        cache.MarkMaterialized(one);

        cache.MarkSubmitted(one, 1);

        Reject<InvalidOperationException>(() => catalog.Remove(first.Output.Resource), "In-flight resource pinned");

        var two = Acquire(cache, second);

        Require(two.DescriptorOffset != one.DescriptorOffset, "In-flight descriptors cannot be overwritten");

        cache.MarkMaterialized(two);

        cache.MarkSubmitted(two, 2);

        Require(cache.TryAcquire(third) is null, "Bounded backpressure");

        Reject<InvalidOperationException>(cache.EnsureIdle, "In-flight cache not idle");
        Reject<ArgumentOutOfRangeException>(() => cache.Collect(ulong.MaxValue), "Device removal sentinel");
        Reject<ArgumentOutOfRangeException>(() => cache.Collect(3), "Unsubmitted completion");

        cache.Collect(1);

        var hit = Acquire(cache, first);

        Require(!hit.RequiresMaterialization && hit.DescriptorOffset == one.DescriptorOffset, "Immutable table reused");

        Reject<InvalidOperationException>(() => cache.Cancel(one), "Stale lease refused");
        Reject<InvalidOperationException>(() => cache.MarkSubmitted(hit, 2), "Nonmonotonic submission");

        cache.MarkSubmitted(hit, 3);

        cache.Collect(2);

        var eviction = Acquire(cache, third);

        Require(eviction.RequiresMaterialization && eviction.DescriptorOffset == two.DescriptorOffset,
            "Only completed table is evicted");

        cache.MarkMaterialized(eviction);

        cache.MarkSubmitted(eviction, 4);

        cache.Collect(4);

        cache.EnsureIdle();

        catalog.Remove(first.Input.Resource);
        catalog.Remove(first.Output.Resource);

        Require(cache.Hits == 1 && cache.Misses == 3 && cache.Materializations == 3 && cache.Backpressure == 1,
            "Cache counters");

        Reject<ArgumentOutOfRangeException>(() => cache.Collect(3), "Regressing completion");
    }

    private static void TestCancellation()
    {
        var catalog = new DescriptorBindingCatalog(2);
        var table = CreateTable(catalog);
        var cache = new DescriptorTableCache(catalog, 1);
        var other = new DescriptorTableCache(catalog, 1);
        var lease = Acquire(cache, table);
        var unrelated = Acquire(other, table);

        Reject<InvalidOperationException>(() => cache.Cancel(unrelated), "Foreign lease refused");

        other.Cancel(unrelated);

        cache.Cancel(lease);

        var retry = Acquire(cache, table);

        Require(retry.RequiresMaterialization, "Cancelled unfinished table must be rewritten");

        cache.MarkMaterialized(retry);

        cache.Cancel(retry);

        var ready = Acquire(cache, table);

        Require(!ready.RequiresMaterialization, "Cancelled completed materialization can be reused");

        cache.Cancel(ready);

        catalog.Remove(table.Input.Resource);

        Reject<InvalidOperationException>(() => cache.TryAcquire(table), "Cache cannot revive stale generation");

        cache.EnsureIdle();
        other.EnsureIdle();
    }

    private static ComputeBufferTable CreateTable(DescriptorBindingCatalog catalog)
    {
        var input = catalog.Register(1, 1024, false);
        var output = catalog.Register(2, 16, true);

        return new ComputeBufferTable(
            new BufferDescriptor(input, BufferViewKind.Constant, 0, 256),
            new BufferDescriptor(input, BufferViewKind.ShaderResource, 512, 16),
            new BufferDescriptor(output, BufferViewKind.UnorderedAccess, 0, 16));
    }

    private static DescriptorTableCacheLease Acquire(DescriptorTableCache cache, ComputeBufferTable table)
    {
        return cache.TryAcquire(table) ?? throw new InvalidOperationException("Expected available descriptor table.");
    }

    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}: {message}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
