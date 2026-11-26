// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    public static void VerifyRootSignatureAbi(nint device)
    {
        var create = (delegate* unmanaged[Stdcall]<nint, uint, byte*, nuint, Guid*, nint*, int>)Slot(device, 16);
        var id = RootSignatureId;
        var root = (nint)0;
        var parent = (nint)0;
        var queried = (nint)0;
        // Synthetic opaque data, intentionally not a GPU-validated root signature.
        var bytes = stackalloc byte[8] { 0x44, 0x58, 0x42, 0x43, 1, 2, 3, 4 };

        try
        {
            if (create(device, 0, bytes, 8, &id, &root) != 0 || !IsRootSignature(root))
            {
                throw new InvalidOperationException("Root signature creation failed.");
            }

            bytes[4] = 99;

            if (RootSignatureBlob(root).Length != 8 || RootSignatureBlob(root)[4] != 1)
            {
                throw new InvalidOperationException("Root signature does not own its blob.");
            }

            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(root, 0);
            var child = ChildId;

            if (query(root, &child, &queried) != 0 || queried != root)
            {
                throw new InvalidOperationException("Root signature QueryInterface failed.");
            }

            _ = Drop(queried);

            queried = 0;

            var pageable = PageableId;

            if (query(root, &pageable, &queried) != NoInterface || queried != 0)
            {
                throw new InvalidOperationException("Root signature incorrectly supports pageable interface.");
            }

            var getDevice = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(root, 7);
            var deviceId = DeviceId;

            if (getDevice(root, &deviceId, &parent) != 0 || parent != device)
            {
                throw new InvalidOperationException("Root signature parent device failed.");
            }

            var bad = (nint)1;

            if (create(device, 0, null, 0, &id, &bad) != InvalidArgument || bad != 0)
            {
                throw new InvalidOperationException("Root signature accepted an invalid blob.");
            }

            if (create(device, 0, bytes, 8, &pageable, &bad) != NoInterface || bad != 0)
            {
                throw new InvalidOperationException("Root signature accepted an unsupported IID.");
            }

            var old = root;

            if (Drop(root) != 0)
            {
                root = 0;

                throw new InvalidOperationException("Root signature reference count did not reach zero.");
            }

            root = 0;

            if (IsRootSignature(old))
            {
                throw new InvalidOperationException("Root signature registry retained a released object.");
            }
        }
        finally
        {
            if (queried != 0)
            {
                _ = Drop(queried);
            }

            if (parent != 0)
            {
                _ = Drop(parent);
            }

            if (root != 0)
            {
                _ = Drop(root);
            }
        }
    }
}
