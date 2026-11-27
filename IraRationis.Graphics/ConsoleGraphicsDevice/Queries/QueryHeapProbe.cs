// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Queries.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    internal static void VerifyQueryHeapAbi(nint device)
    {
        var create = (delegate* unmanaged[Stdcall]<nint, QueryHeapDescription*, Guid*, nint*, int>)Slot(device, 39);
        var description = new QueryHeapDescription { Kind = 1, Count = 64, Nodes = 1 };
        var iid = QueryHeapId;
        var rejectedId = Guid.Empty;
        var heap = (nint)0;
        var queried = (nint)0;
        var parent = (nint)0;

        try
        {
            if (create(device, &description, &iid, &heap) != 0 || !IsQueryHeap(heap))
            {
                throw new InvalidOperationException("Query heap creation failed.");
            }

            description.Count = 0;

            var saved = ((QueryHeapObject*)heap)->Description;

            if (saved.Kind != 1 || saved.Count != 64 || saved.Nodes != 1)
            {
                throw new InvalidOperationException("Query heap description ownership failed.");
            }

            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(heap, 0);
            var pageable = PageableId;

            if (query(heap, &pageable, &queried) != 0 || queried != heap)
            {
                throw new InvalidOperationException("Query heap QueryInterface failed.");
            }

            var refs = Drop(queried);

            queried = 0;

            if (refs != 1)
            {
                throw new InvalidOperationException("Query heap reference count failed.");
            }

            var getDevice = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(heap, 7);
            var deviceId = DeviceId;

            if (getDevice(heap, &deviceId, &parent) != 0 || parent != device)
            {
                throw new InvalidOperationException("Query heap parent device failed.");
            }

            _ = Drop(parent);

            parent = 0;

            var rejected = (nint)123;

            if (create(device, null, &iid, &rejected) != InvalidArgument || rejected != 0)
            {
                throw new InvalidOperationException("Query heap null description validation failed.");
            }

            rejected = 123;

            if (create(device, &description, &rejectedId, &rejected) != NoInterface || rejected != 0)
            {
                throw new InvalidOperationException("Query heap IID validation failed.");
            }

            var address = heap;

            refs = Drop(heap);

            heap = 0;

            if (refs != 0 || IsQueryHeap(address))
            {
                throw new InvalidOperationException("Query heap lifetime failed.");
            }
        }
        finally
        {
            if (parent != 0)
            {
                _ = Drop(parent);
            }

            if (queried != 0)
            {
                _ = Drop(queried);
            }

            if (heap != 0)
            {
                _ = Drop(heap);
            }
        }
    }
}
