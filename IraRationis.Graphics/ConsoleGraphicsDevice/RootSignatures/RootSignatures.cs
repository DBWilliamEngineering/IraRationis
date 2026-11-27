// SPDX-License-Identifier: GPL-3.0-or-later
// Console object ABI adapted from Zettrox/ztrx victory-gpu/src/root.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.RootSignatures.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static unsafe partial class ConsoleGraphicsDevice
{
    // Defensive limit for a serialized signature, not a hardware limit.
    private const int MaximumRootSignatureBytes = 16 * 1024 * 1024;
    private static readonly Guid RootSignatureId = new("c54a6b66-72df-4ee8-8be5-a946a1429214");
    private static nint _rootSignatureTable;

    private static bool IsRootSignature(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _rootSignatureTable;
    }

    private static ReadOnlySpan<byte> RootSignatureBlob(nint value)
    {
        var root = (RootSignatureObject*)value;

        return new ReadOnlySpan<byte>(root + 1, checked((int)root->BlobSize));
    }

    private static void RegisterRootSignatureMethods(nint* device)
    {
        if (sizeof(RootSignatureObject) != 24)
        {
            Environment.FailFast("Unexpected root signature ABI header size.");
        }

        _rootSignatureTable = (nint)ChildTable(24);

        device[16] =
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, byte*, nuint, Guid*, nint*, int>)&CreateRootSignature;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateRootSignature(nint self, uint nodes, byte* blob, nuint size, Guid* iid, nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (blob == null || size == 0 || size > MaximumRootSignatureBytes)
        {
            return InvalidArgument;
        }

        if (iid == null || (*iid != RootSignatureId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId))
        {
            return NoInterface;
        }

        if (output == null)
        {
            return 1; // S_FALSE: no object requested.
        }

        RootSignatureObject* root = null;

        try
        {
            root = (RootSignatureObject*)NativeMemory.AllocZeroed((nuint)sizeof(RootSignatureObject) + size);

            root->Header.Table = _rootSignatureTable;

            root->Header.References = 1;

            root->BlobSize = size;

            new ReadOnlySpan<byte>(blob, (int)size).CopyTo(new Span<byte>(root + 1, (int)size));

            LiveObjects[(nint)root] = _rootSignatureTable;
        }
        catch (OutOfMemoryException)
        {
            if (root != null)
            {
                NativeMemory.Free(root);
            }

            return OutOfMemory;
        }

        *output = (nint)root;

        Console.WriteLine(
            $"[graphics] Root signature: bytes={size}, nodes=0x{nodes:X}, object=0x{(nuint)root:X} (CPU blob only).");

        return 0;
    }

    private static void ReleaseRootSignature(nint value)
    {
        _ = LiveObjects.TryRemove(value, out _);

        foreach (var key in ResourcePrivateData.Keys)
        {
            if (key.Item1 == value)
            {
                _ = ResourcePrivateData.TryRemove(key, out _);
            }
        }

        NativeMemory.Free((void*)value);
    }
}
