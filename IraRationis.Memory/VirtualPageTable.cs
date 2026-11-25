// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Memory;

public enum MemoryResidency
{
    SystemUpload,
    MirroredLocal,
    GpuUpload,
    ExistingSystemHeap
}

public readonly record struct MemoryPage(nuint CpuAddress, ulong GpuAddress, MemoryResidency Residency);

/// <summary>Software translation metadata. This does not change CPU or GPU hardware page tables.</summary>
public sealed class VirtualPageTable
{
    public const ulong PageSize = 65536;
    private readonly Lock _gate = new();

    private readonly Dictionary<ulong, MemoryPage> _pages = [];

    public void Map(ulong guestBase, nuint cpuBase, ulong gpuBase, ulong bytes, MemoryResidency residency)
    {
        if (guestBase % PageSize != 0 || bytes == 0 || bytes % PageSize != 0 || cpuBase == 0 || gpuBase == 0)
        {
            throw new ArgumentException("Expected nonempty, page-aligned guest range and valid host mappings.");
        }

        _ = checked(guestBase + bytes - 1);
        _ = checked(cpuBase + (nuint)(bytes - 1));
        _ = checked(gpuBase + bytes - 1);

        lock (_gate)
        {
            for (var offset = 0UL; offset < bytes; offset += PageSize)
            {
                if (_pages.ContainsKey((guestBase + offset) / PageSize))
                {
                    throw new InvalidOperationException("Guest range overlaps an existing mapping.");
                }
            }

            for (var offset = 0UL; offset < bytes; offset += PageSize)
            {
                _pages.Add((guestBase + offset) / PageSize,
                    new MemoryPage(cpuBase + (nuint)offset, gpuBase + offset, residency));
            }
        }
    }

    public bool TryTranslate(ulong guestAddress, out MemoryPage page)
    {
        lock (_gate)
        {
            if (!_pages.TryGetValue(guestAddress / PageSize, out var entry))
            {
                page = default;

                return false;
            }

            var offset = guestAddress % PageSize;
            page = new MemoryPage(entry.CpuAddress + (nuint)offset, entry.GpuAddress + offset, entry.Residency);

            return true;
        }
    }

    public void Unmap(ulong guestBase, ulong bytes)
    {
        if (guestBase % PageSize != 0 || bytes == 0 || bytes % PageSize != 0)
        {
            throw new ArgumentException("Expected a nonempty page-aligned guest range.");
        }

        _ = checked(guestBase + bytes - 1);

        lock (_gate)
        {
            for (var offset = 0UL; offset < bytes; offset += PageSize)
            {
                _pages.Remove((guestBase + offset) / PageSize);
            }
        }
    }
}
