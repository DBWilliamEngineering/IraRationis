// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DescriptorProbeDescription
    {
        public DescriptorHeapDescription Description;
        public DescriptorPlacement Placement;
    }

    internal static void VerifyDescriptorAbi(nint device)
    {
        var create = (delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, Guid*, nint*, int>)Slot(device, 14);
        var increment = (delegate* unmanaged[Stdcall]<nint, uint, uint>)Slot(device, 15);
        var cbv = (delegate* unmanaged[Stdcall]<nint, uint*, ulong, void>)Slot(device, 17);
        var srv = (delegate* unmanaged[Stdcall]<nint, nint, uint*, ulong, void>)Slot(device, 18);
        var copy = (delegate* unmanaged[Stdcall]<nint, uint, ulong, ulong, uint, void>)Slot(device, 24);
        var ranges = (delegate* unmanaged[Stdcall]<nint, uint, ulong*, uint*, uint, ulong*, uint*, uint, void>)Slot(device, 23);
        var id = DescriptorHeapId;
        var description = new DescriptorProbeDescription { Description = new DescriptorHeapDescription { Kind = 0, Count = 8, Flags = 1, Nodes = 1 }, Placement = default };
        nint heap = 0;
        nint borrowed = 0;
        var memory = NativeMemory.AlignedAlloc(256, 64);
        try
        {
            if (increment(device, 0) != 32 || increment(device, 1) != 16 || increment(device, 2) != 128 || increment(device, 3) != 128 || increment(device, 4) != 0)
            {
                throw new InvalidOperationException("Descriptor stride mismatch.");
            }
            CheckResource(create(device, &description.Description, &id, &heap), "descriptor heap");
            var start = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(heap, 9))(heap);
            var gpu = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(heap, 10))(heap);
            var actual = default(DescriptorHeapDescription);
            ((delegate* unmanaged[Stdcall]<nint, DescriptorHeapDescription*, void>)Slot(heap, 8))(heap, &actual);
            if (start == 0 || gpu != start || start % 64 != 0 || actual.Count != 8 || *(ulong*)start != 0)
            {
                throw new InvalidOperationException("Descriptor heap layout mismatch.");
            }
            var constant = stackalloc uint[4] { 0x1000, 1, 256, 0 };
            cbv(device, constant, start);
            srv(device, 0, null, start + 32);
            srv(device, 0, null, start + 64);
            var first = Unsafe.ReadUnaligned<ConstantDescriptor>((void*)start);
            var second = Unsafe.ReadUnaligned<DescriptorToken>((void*)(start + 32));
            var third = Unsafe.ReadUnaligned<DescriptorToken>((void*)(start + 64));
            if (first.Tag != ConstantTag || first.Address != 0x100001000UL || first.Size != 256 || second.Tag != DescriptorTag || second.Id == 0 || second.Id != third.Id)
            {
                throw new InvalidOperationException("Descriptor record mismatch.");
            }
            copy(device, 2, start + 32, start, 0);
            if (!new ReadOnlySpan<byte>((void*)start, 32).SequenceEqual(new ReadOnlySpan<byte>((void*)(start + 32), 32)) || Unsafe.ReadUnaligned<DescriptorToken>((void*)(start + 64)).Id != second.Id)
            {
                throw new InvalidOperationException("Overlapping descriptor copy mismatch.");
            }
            var destination = start + 128;
            var source = stackalloc ulong[2] { start, start + 64 };
            var destinationCount = 2U;
            ranges(device, 1, &destination, &destinationCount, 2, source, null, 0);
            if (Unsafe.ReadUnaligned<ConstantDescriptor>((void*)destination).Address != first.Address || Unsafe.ReadUnaligned<DescriptorToken>((void*)(destination + 32)).Id != second.Id)
            {
                throw new InvalidOperationException("Gather/scatter descriptor copy mismatch.");
            }
            if (_newer)
            {
                NativeMemory.Clear(memory, 256);
                description.Placement = new DescriptorPlacement { Address = (ulong)memory, Size = 256 };
                CheckResource(create(device, &description.Description, &id, &borrowed), "borrowed descriptor heap");
                if (((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(borrowed, 9))(borrowed) != (ulong)memory)
                {
                    throw new InvalidOperationException("Borrowed descriptor address mismatch.");
                }
                cbv(device, constant, (ulong)memory);
                ReleaseProbe(ref borrowed);
                if (Unsafe.ReadUnaligned<ConstantDescriptor>(memory).Tag != ConstantTag)
                {
                    throw new InvalidOperationException("Borrowed descriptor data did not survive heap release.");
                }
            }
        }
        finally
        {
            ReleaseProbe(ref borrowed);
            ReleaseProbe(ref heap);
            NativeMemory.AlignedFree(memory);
        }
    }
}
