// SPDX-License-Identifier: GPL-3.0-or-later
// Allocation attribute encoding adapted from Zettrox/ztrx, victory-gpu/src/resource.rs.
using System.Numerics;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static Func<(nint Allocate, nint Free)>? _resourceAllocator;

    // Register before image execution. Resolve the pair at allocation time, after mapping.
    public static void ConfigureResourceAllocator(Func<(nint Allocate, nint Free)> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resourceAllocator = resolver;
    }

    private static ulong ResourceAllocationAttributes(ulong size, ulong alignment, uint heapKind)
    {
        const ulong graphicsAllocator = 128UL << 16;
        var pages = size >= (2UL << 20) ? 2UL : 1UL;
        var memory = heapKind == 1 ? 1UL : 3UL;
        var exponent = (ulong)BitOperations.TrailingZeroCount(alignment);
        return graphicsAllocator | (pages << 14) | (exponent << 24) | (memory << 29);
    }

    private static void FreeCommittedBacking(nint memory, nint freeHook, ulong attributes)
    {
        if (memory == 0)
        {
            return;
        }
        if (freeHook != 0)
        {
            ((delegate* unmanaged[Stdcall]<void*, ulong, void>)freeHook)((void*)memory, attributes);
            return;
        }
        NativeMemory.AlignedFree((void*)memory);
    }
}
