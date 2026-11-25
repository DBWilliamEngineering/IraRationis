// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using IraRationis.Graphics.Textures;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    internal static void VerifyResourceAbi(nint device)
    {
        var desc = new ResourceDescription
        {
            Dimension = 1,
            Alignment = ResourcePage,
            Width = ResourcePage,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 0,
            SampleCount = 1,
            SampleQuality = 0,
            Layout = 1,
            Flags = 0
        };
        var id = ResourceId;
        var heapId = HeapId;
        var memory = NativeMemory.AlignedAlloc((nuint)ResourcePage, (nuint)ResourcePage);
        var view = (nint)0;
        var heap = (nint)0;
        var placed = (nint)0;
        var alias = (nint)0;
        var committed = (nint)0;
        try
        {
            var create = (delegate* unmanaged[Stdcall]<nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(device, 133);
            CheckResource(create(device, (ulong)memory, &desc, 0, 0, &id, &view), "create at address");
            var address = (delegate* unmanaged[Stdcall]<nint, ulong>)Slot(view, 11);
            var map = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Slot(view, 8);
            var mapped = (nint)0;
            CheckResource(map(view, 0, 0, &mapped), "map borrowed address");
            if (mapped != (nint)memory || address(view) != (ulong)memory)
            {
                throw new InvalidOperationException("Resource identity mismatch.");
            }
            *(uint*)mapped = 0x12345678;
            ReleaseProbe(ref view);
            if (*(uint*)memory != 0x12345678)
            {
                throw new InvalidOperationException("Borrowed memory was not preserved.");
            }

            var info = default(AllocationInfo);
            var footprint = (delegate* unmanaged[Stdcall]<AllocationInfo*, nint, uint, uint, ResourceDescription*, AllocationInfo*>)Slot(device, 25);
            if (footprint(&info, device, 0, 1, &desc) != &info || info.Size != ResourcePage || info.Alignment != ResourcePage)
            {
                throw new InvalidOperationException("Buffer footprint mismatch.");
            }
            VerifyTextureFootprints(device);
            var properties = new HeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };
            var heapDesc = new HeapDescription { Size = ResourcePage * 2, Alignment = ResourcePage, Properties = properties, Flags = 0 };
            var createHeap = (delegate* unmanaged[Stdcall]<nint, HeapDescription*, Guid*, nint*, int>)Slot(device, 28);
            CheckResource(createHeap(device, &heapDesc, &heapId, &heap), "create heap");
            var createPlaced = (delegate* unmanaged[Stdcall]<nint, nint, ulong, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(device, 29);
            CheckResource(createPlaced(device, heap, ResourcePage, &desc, 0, 0, &id, &placed), "placed buffer");
            CheckResource(createPlaced(device, heap, ResourcePage, &desc, 0, 0, &id, &alias), "placed alias");
            var first = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(placed, 11))(placed);
            var second = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(alias, 11))(alias);
            ReleaseProbe(ref heap);
            *(uint*)first = 0xA1B2C3D4;
            if (first != second || *(uint*)second != 0xA1B2C3D4)
            {
                throw new InvalidOperationException("Heap alias or ownership mismatch.");
            }
            var createCommitted = (delegate* unmanaged[Stdcall]<nint, HeapProperties*, uint, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(device, 27);
            CheckResource(createCommitted(device, &properties, 0, &desc, 0, 0, &id, &committed), "committed buffer");
            var committedAddress = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(committed, 11))(committed);
            if (committedAddress == 0 || committedAddress % ResourcePage != 0 || *(uint*)committedAddress != 0)
            {
                throw new InvalidOperationException("Committed buffer alignment or initial contents mismatch.");
            }
            var pool = (nuint)0;
            var register = (delegate* unmanaged[Stdcall]<nint, ulong, uint, nuint*, int>)Slot(device, 74);
            var unregister = (delegate* unmanaged[Stdcall]<nint, nuint, int>)Slot(device, 75);
            CheckResource(register(device, (ulong)memory, 1, &pool), "register pool");
            CheckResource(unregister(device, pool), "unregister pool");
            if (unregister(device, pool) >= 0)
            {
                throw new InvalidOperationException("Invalid pool handle accepted.");
            }
        }
        finally
        {
            ReleaseProbe(ref view);
            ReleaseProbe(ref placed);
            ReleaseProbe(ref alias);
            ReleaseProbe(ref heap);
            ReleaseProbe(ref committed);
            NativeMemory.AlignedFree(memory);
        }
    }

    private static void VerifyTextureFootprints(nint device)
    {
        var description = new ResourceDescription
        {
            Dimension = 3,
            Width = 3840,
            Height = 2160,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = 28,
            SampleCount = 1,
            Flags = 0x300005
        };
        CheckTextureSize(description, 0x1FE0000, 65536);
        var info = default(AllocationInfo);
        var footprint = (delegate* unmanaged[Stdcall]<AllocationInfo*, nint, uint, uint, ResourceDescription*, AllocationInfo*>)Slot(device, 25);
        if (footprint(&info, device, 0, 1, &description) != &info || info.Size != 0x1FE0000 || info.Alignment != 65536)
        {
            throw new InvalidOperationException("4K render-target allocation ABI mismatch.");
        }
        description.Width = 64;
        description.Height = 64;
        description.Layout = 1;
        CheckTextureSize(description, 16384, 256);
        description.Layout = 0;
        description.Flags = 0;
        CheckTextureSize(description, 16384, 4096);
        description.Width = 256;
        description.Height = 256;
        description.Format = 98;
        description.Layout = 0x10A;
        CheckTextureSize(description, 65536, 65536);
        description.Width = 128;
        description.Height = 128;
        description.Format = 28;
        description.MipLevels = 8;
        CheckTextureSize(description, 131072, 65536);
        description.MipLevels = 1;
        description.Format = 45;
        description.Layout = 0;
        description.Flags = 2;
        CheckTextureSize(description, 131072, 65536);
        description.Format = 28;
        description.Flags = 1;
        description.SampleCount = 4;
        CheckTextureSize(description, 262144, 65536);
        description.SampleCount = 1;
        description.Width = 16385;
        if (TextureFootprintCalculator.TryCalculate(description, out _, out _))
        {
            throw new InvalidOperationException("Out-of-range texture extent accepted.");
        }
        description.Width = 64;
        description.Height = 64;
        description.Layout = 1;
        description.Format = 0;
        if (TextureFootprintCalculator.TryCalculate(description, out _, out _))
        {
            throw new InvalidOperationException("Unknown texture format accepted.");
        }
        description.Format = 28;
        description.Alignment = 3;
        if (TextureFootprintCalculator.TryCalculate(description, out _, out _))
        {
            throw new InvalidOperationException("Invalid texture alignment accepted.");
        }
        description.Alignment = 0;
        var properties = new HeapProperties { Kind = 1, CreationNodes = 1, VisibleNodes = 1 };
        var id = ResourceId;
        var texture = (nint)0;
        var create = (delegate* unmanaged[Stdcall]<nint, HeapProperties*, uint, ResourceDescription*, uint, nint, Guid*, nint*, int>)Slot(device, 27);
        try
        {
            CheckResource(create(device, &properties, 0, &description, 0, 0, &id, &texture), "committed texture CPU backing");
            var address = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(texture, 11))(texture);
            if (address == 0 || address % 256 != 0 || *(byte*)(address + 16383) != 0)
            {
                throw new InvalidOperationException("Texture backing alignment or extent mismatch.");
            }
        }
        finally
        {
            ReleaseProbe(ref texture);
        }
    }

    private static void CheckTextureSize(ResourceDescription description, ulong expectedSize, ulong expectedAlignment)
    {
        if (!TextureFootprintCalculator.TryCalculate(description, out var size, out var alignment) || size != expectedSize || alignment != expectedAlignment)
        {
            throw new InvalidOperationException($"Texture footprint mismatch: expected {expectedSize}/{expectedAlignment}, got {size}/{alignment}.");
        }
    }

    private static void ReleaseProbe(ref nint value)
    {
        if (value != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2))(value);
            value = 0;
        }
    }

    private static void CheckResource(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"Resource probe {operation}: 0x{hr:X8}.");
        }
    }
}
