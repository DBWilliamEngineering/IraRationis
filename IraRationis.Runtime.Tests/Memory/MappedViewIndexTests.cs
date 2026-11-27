// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Memory;

namespace IraRationis.Runtime.Tests.Memory;

internal static class MappedViewIndexTests
{
    public static void Run()
    {
        TestBoundaries();
        TestSnapshotOwnership();
        TestRandomizedRanges();
        TestFragmentedAddressSpace();
    }

    private static void TestBoundaries()
    {
        var index = new MappedViewIndex<object>();
        var first = new object();
        var adjacent = new object();
        var last = new object();

        Require(!index.TryGetContaining(0, out _), "Empty index lookup");
        Require(index.SnapshotOverlaps(0, 1).Length == 0, "Empty index snapshot");

        index.Add(0, 10, first);
        index.Add(10, 5, adjacent);
        index.Add(ulong.MaxValue - 10, 10, last);

        Require(index.TryGetContaining(0, out var found) && ReferenceEquals(found.Value, first), "Zero address");
        Require(index.TryGetContaining(9, out found) && ReferenceEquals(found.Value, first), "Last included byte");
        Require(index.TryGetContaining(10, out found) && ReferenceEquals(found.Value, adjacent), "Adjacent view");
        Require(!index.TryGetContaining(15, out _), "Exclusive upper bound");
        Require(!index.TryGetContaining(ulong.MaxValue, out _), "Maximum address excluded");
        Require(index.SnapshotOverlaps(10, 5).Length == 1, "Touching view excluded");
        Require(index.SnapshotOverlaps(9, 2).Length == 2, "Predecessor and next view overlap");
        Require(index.SnapshotOverlaps(ulong.MaxValue - 1, 1).Single().Value == last, "High address overlap");
        Require(index.SnapshotOverlaps(ulong.MaxValue, 0).Length == 0, "Empty high address range");

        RequireRejected(() => index.Add(5, 1, new object()), "Contained insertion");
        RequireRejected(() => index.Add(0, 1, new object()), "Duplicate insertion");
        RequireRejected(() => index.Add(20, ulong.MaxValue, new object()), "Insertion overflow");
        RequireRejected(() => index.Add(20, 0, new object()), "Empty insertion");
        RequireRejected(() => index.SnapshotOverlaps(ulong.MaxValue, 1), "Query overflow");

        var gapIndex = new MappedViewIndex<object>();

        gapIndex.Add(100, 10, first);
        RequireRejected(() => gapIndex.Add(90, 30, adjacent), "Insertion covering successor");
        Require(gapIndex.SnapshotOverlaps(0, 100).Length == 0, "View at query end excluded");
    }

    private static void TestSnapshotOwnership()
    {
        var index = new MappedViewIndex<object>();
        var original = new object();

        index.Add(100, 100, original);

        var snapshot = index.SnapshotOverlaps(120, 30);

        Require(index.Remove(100), "Remove existing view");
        Require(!index.Remove(100), "Remove absent view");

        index.Add(100, 20, original);
        index.Add(150, 50, original);

        Require(snapshot.Length == 1 && snapshot[0].Key == 100 && ReferenceEquals(snapshot[0].Value, original),
            "Snapshot retains original value across remove and split");
        Require(index.SnapshotOverlaps(120, 30).Length == 0, "Split leaves requested gap");
        Require(index.TryGetContaining(119, out _) && index.TryGetContaining(150, out _), "Split retains both sides");
    }

    private static void TestRandomizedRanges()
    {
        var random = new Random(7331);
        var index = new MappedViewIndex<object>();
        var reference = new Dictionary<ulong, (ulong Size, object Value)>();

        for (var iteration = 0; iteration < 4000; iteration++)
        {
            var address = (ulong)random.Next(512) * 64;
            var size = (ulong)random.Next(1, 5) * 64;

            if (random.Next(3) == 0)
            {
                Require(index.Remove(address) == reference.Remove(address), "Random removal agrees with model");
            }
            else if (!reference.Any(pair => pair.Key < address + size && pair.Key + pair.Value.Size > address))
            {
                var value = new object();

                index.Add(address, size, value);
                reference.Add(address, (size, value));
            }

            var queryAddress = (ulong)random.Next(32768);
            var querySize = (ulong)random.Next(1024);
            var expected = reference.Where(pair => querySize != 0 && pair.Key < queryAddress + querySize
                                                                  && pair.Key + pair.Value.Size > queryAddress)
                .OrderBy(pair => pair.Key).ToArray();
            var actual = index.SnapshotOverlaps(queryAddress, querySize);

            Require(actual.Length == expected.Length, "Random overlap count agrees with linear model");

            for (var match = 0; match < expected.Length; match++)
            {
                Require(actual[match].Key == expected[match].Key
                        && ReferenceEquals(actual[match].Value, expected[match].Value.Value),
                    "Random overlap identity and ordering agree with linear model");
            }

            var containing = reference.Where(pair => pair.Key <= queryAddress
                                                     && queryAddress - pair.Key < pair.Value.Size).ToArray();
            var found = index.TryGetContaining(queryAddress, out var view);

            Require(found == (containing.Length == 1), "Random containment agrees with linear model");

            if (found)
            {
                Require(view.Key == containing[0].Key && ReferenceEquals(view.Value, containing[0].Value.Value),
                    "Random containing view identity");
            }
        }
    }

    private static void TestFragmentedAddressSpace()
    {
        var index = new MappedViewIndex<object>();
        var value = new object();
        const ulong pageSize = 65536;

        for (var page = 0; page < 8192; page++)
        {
            index.Add((ulong)page * pageSize * 2, pageSize, value);
        }

        var address = 8190 * pageSize * 2;
        var overlaps = index.SnapshotOverlaps(address + pageSize - 1, pageSize + 2);

        Require(overlaps.Length == 2 && overlaps[0].Key == address && overlaps[1].Key == address + pageSize * 2,
            "Sparse high-address query returns only intersecting views");
        Require(!index.TryGetContaining(address + pageSize, out _), "Sparse gap is unmapped");
    }

    private static void RequireRejected(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
