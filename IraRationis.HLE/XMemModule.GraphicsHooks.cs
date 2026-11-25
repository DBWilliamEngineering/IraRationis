// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE;

public static partial class XMemModule
{
    internal static (nint Allocate, nint Free) GetGraphicsAllocationHooks()
    {
        return (GetHook(0), GetHook(1));
    }
}
