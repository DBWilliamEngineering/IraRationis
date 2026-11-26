// SPDX-License-Identifier: GPL-3.0-or-later
// Console ABI adapted from Zettrox/ztrx victory-gpu/src/signature.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private static readonly Guid CommandSignatureId = new("c36a797c-ec80-4f0a-8985-a7b2475082d1");
    private static nint _commandSignatureTable;

    private static bool IsCommandSignature(nint value)
    {
        return value != 0 && LiveObjects.TryGetValue(value, out var table) && table == _commandSignatureTable;
    }

    private static void RegisterCommandSignatureMethods(nint* device)
    {
        if (sizeof(CommandSignatureDescription) != 24 || sizeof(CommandSignatureObject) != 40)
        {
            throw new InvalidOperationException("Unexpected command signature ABI layout.");
        }

        _commandSignatureTable = (nint)ChildTable(24);

        device[41] =
            (nint)(delegate* unmanaged[Stdcall]<nint, CommandSignatureDescription*, nint, Guid*, nint*, int>)
            &CreateCommandSignature;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CreateCommandSignature(nint self, CommandSignatureDescription* description, nint root, Guid* iid,
        nint* output)
    {
        if (output != null)
        {
            *output = 0;
        }

        if (description == null || description->Count > 65536
                                || (description->Count != 0 && description->Arguments == null)
                                || (root != 0 && !IsRootSignature(root)))
        {
            return InvalidArgument;
        }

        if (iid == null || (*iid != CommandSignatureId && *iid != UnknownId && *iid != ObjectId && *iid != ChildId
                            && *iid != PageableId))
        {
            return NoInterface;
        }

        if (output == null)
        {
            return 1;
        }

        CommandSignatureObject* signature = null;

        var retained = false;

        try
        {
            var words = checked((int)description->Count * 4);

            signature = (CommandSignatureObject*)NativeMemory
                .AllocZeroed(
                    (nuint)sizeof(CommandSignatureObject) + (nuint)words * sizeof(uint));

            signature->Header.Table = _commandSignatureTable;
            signature->Header.References = 1;
            signature->Stride = description->Stride;
            signature->Count = description->Count;
            signature->Nodes = description->Nodes;
            signature->Root = root;

            new ReadOnlySpan<uint>(description->Arguments, words).CopyTo(new Span<uint>(signature + 1, words));

            if (root != 0)
            {
                _ = Retain(root);

                retained = true;
            }

            LiveObjects[(nint)signature] = _commandSignatureTable;
        }
        catch (OutOfMemoryException)
        {
            if (retained)
            {
                _ = Drop(root);
            }

            NativeMemory.Free(signature);

            return OutOfMemory;
        }

        *output = (nint)signature;

        Console.WriteLine(
            $"[graphics] Command signature: stride={signature->Stride}, arguments={signature->Count}, nodes=0x{signature->Nodes:X} (CPU layout only).");

        return 0;
    }

    private static void ReleaseCommandSignature(nint value)
    {
        var root = ((CommandSignatureObject*)value)->Root;

        _ = LiveObjects.TryRemove(value, out _);

        foreach (var key in ResourcePrivateData.Keys)
        {
            if (key.Item1 == value)
            {
                _ = ResourcePrivateData.TryRemove(key, out _);
            }
        }

        NativeMemory.Free((void*)value);

        if (root != 0)
        {
            _ = Drop(root);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandSignatureDescription
    {
        public uint Stride;
        public uint Count;
        public uint* Arguments;
        public uint Nodes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommandSignatureObject
    {
        public Header Header;
        public uint Stride;
        public uint Count;
        public uint Nodes;
        public nint Root;
    }
}
