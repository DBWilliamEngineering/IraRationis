// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics;

namespace IraRationis.HLE;

public static class GraphicsDeviceModule
{
    public static HleModule Create()
    {
        ConsoleGraphicsDevice.ConfigureResourceAllocator(XMemModule.GetGraphicsAllocationHooks);

        ConsoleGraphicsDevice.ConfigurePageMapper(PhysicalPageModule.MapAlias);

        var address = ConsoleGraphicsDevice.ExportAddress;

        return new HleModule("d3d12-device", ["d3d12_xs.dll"],
        [
            new HleExport("D3D12XboxCreateDevice", (nuint)address)
        ]);
    }
}
