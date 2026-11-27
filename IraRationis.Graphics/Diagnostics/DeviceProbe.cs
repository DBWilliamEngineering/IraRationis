// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Textures;
using IraRationis.Shaders.Native;
using IraRationis.Shaders.Raster;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace IraRationis.Graphics.Diagnostics;

public static unsafe class DeviceProbe
{
    public static int Execute(TextWriter writer)
    {
        NativeShaderProbe.Verify();

        writer.WriteLine("Native shader extraction and scalar diagnostic lowering passed (CPU only).");

        PlanarColorShaderCompiler.VerifyAuthoredContracts();

        writer.WriteLine("Planar color shader and resource contracts passed (CPU only).");

        TextureComputerProbe.Verify();

        writer.WriteLine("XG texture computer ABI and layout tests passed.");

        ConsoleGraphicsDevice.VerifyAbiInitialization();

        writer.WriteLine("ABI initialization, null-table diagnostics and root signature registration passed.");

        var create = (delegate* unmanaged[Stdcall]<nint, uint*, Guid*, nint*, int>)ConsoleGraphicsDevice.ExportAddress;
        var version = 0x00020004U;
        var deviceId = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
        var device11Id = new Guid("5405c344-d457-444e-b4dd-2366e45aee39");
        var queueId = new Guid("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
        var fenceId = new Guid("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
        var allocatorId = new Guid("6102dee4-af59-4b09-b999-b44d73f09b24");
        var device = (nint)0;
        var queue = (nint)0;
        var fence = (nint)0;
        var allocator = (nint)0;
        var list = (nint)0;
        var listId = new Guid("c3827890-4024-474c-98cf-645b51dc4874");

        var listDesc = stackalloc uint[4];
        var heap = stackalloc uint[5];
        var blend = stackalloc float[4];

        try
        {
            foreach (var newer in new[] { false, true })
            {
                var id = newer ? device11Id : deviceId;

                Check(create(0, &version, &id, &device), "Device creation");

                if (device == 0 || *((byte*)device + 0x18) == 0)
                {
                    throw new InvalidOperationException("Console device layout check failed.");
                }

                var heapProperties = (delegate* unmanaged[Stdcall]<uint*, nint, uint, uint, uint*>)Slot(device, 26);

                if (heapProperties(heap, device, 0, 2) != heap || heap[0] != 4 || heap[1] != 3 || heap[2] != 1
                    || heap[3] != 1 || heap[4] != 1)
                {
                    throw new InvalidOperationException("Console heap property ABI check failed.");
                }

                VerifyFrameAndDisplay(device);

                ConsoleGraphicsDevice.VerifyResourceAbi(device);

                ConsoleGraphicsDevice.VerifyDescriptorAbi(device);

                ConsoleGraphicsDevice.VerifyRootSignatureAbi(device);

                ConsoleGraphicsDevice.VerifyComputePipelineAbi(device);

                ConsoleGraphicsDevice.VerifyQueryHeapAbi(device);

                ConsoleGraphicsDevice.VerifyDeviceBatchAbi(device);

                writer.WriteLine("Query heap creation, description ownership and interface ABI passed (CPU only).");

                writer.WriteLine("Compute pipeline ABI, shader deduplication and ownership passed (CPU only).");

                writer.WriteLine("Root signature blob ownership and interface ABI passed.");

                writer.WriteLine("Resource CPU backing, aliases, page pools and descriptor ABI passed.");

                var desc = new ProbeQueueDescription { Nodes = 1 };
                var createQueue =
                    (delegate* unmanaged[Stdcall]<nint, ProbeQueueDescription*, Guid*, nint*, int>)Slot(device, 8);

                Check(createQueue(device, &desc, &queueId, &queue), "Queue creation");

                var queueDesc = default(ProbeQueueDescription);
                var describe =
                    (delegate* unmanaged[Stdcall]<nint, ProbeQueueDescription*, void>)Slot(queue, newer ? 28 : 26);

                describe(queue, &queueDesc);

                if (queueDesc.Nodes != 1 || queueDesc.Kind != 0)
                {
                    throw new InvalidOperationException("Console queue description ABI check failed.");
                }

                var createFence = (delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device, 36);

                Check(createFence(device, 0, 0, &fenceId, &fence), "Fence creation");

                ConsoleGraphicsDevice.VerifyFenceAddressHostProbe(device, queue, fence);

                var createAllocator = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(device, 9);

                Check(createAllocator(device, 0, &allocatorId, &allocator), "Allocator creation");

                var reset = (delegate* unmanaged[Stdcall]<nint, int>)Slot(allocator, 8);

                Check(reset(allocator), "Allocator reset");

                new Span<uint>(listDesc, 4).Clear();

                listDesc[2] = 1;

                var createList =
                    (delegate* unmanaged[Stdcall]<nint, uint*, nint, nint, Guid*, nint*, int>)Slot(device, 73);

                Check(createList(device, listDesc, allocator, 0, &listId, &list), "CreateCommandListX");

                if (*(ulong*)(list + 0x18) != 0 || *(ulong*)(list + 0x28) != 0)
                {
                    throw new InvalidOperationException("Command list inline layout check failed.");
                }

                var viewport = new D3D12_VIEWPORT { Width = 1280, Height = 720, MaxDepth = 1 };
                var scissor = default(RECT);

                ((int*)&scissor)[2] = 1280;

                ((int*)&scissor)[3] = 720;

                new Span<float>(blend, 4).Fill(1);

                ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(list, 20))(list, 4);

                ((delegate* unmanaged[Stdcall]<nint, uint, D3D12_VIEWPORT*, void>)Slot(list, 21))(list, 1, &viewport);

                ((delegate* unmanaged[Stdcall]<nint, uint, RECT*, void>)Slot(list, 22))(list, 1, &scissor);

                ((delegate* unmanaged[Stdcall]<nint, float*, void>)Slot(list, 23))(list, blend);

                ((delegate* unmanaged[Stdcall]<nint, uint, void>)Slot(list, 24))(list, 0);

                var syncTop =
                    (delegate* unmanaged[Stdcall]<nint, uint, uint, ulong, ulong, void>)Slot(list, newer ? 131 : 129);
                var syncBottom =
                    (delegate* unmanaged[Stdcall]<nint, uint, uint, ulong, ulong, void>)Slot(list, newer ? 132 : 130);

                syncTop(list, 0, 0, 0, 0);

                syncBottom(list, 0, 0, 0, 0);

                ConsoleGraphicsDevice.VerifyBarrierRecording(list, newer);

                var clearReadback = ConsoleGraphicsDevice.RecordHostClearProbe(device, list);
                // Captured guest pairing and its original typed default, both
                // using real materialization, descriptors, clears and readbacks.
                var linearReadback = ConsoleGraphicsDevice.RecordHostClearProbe(device, list, 29, 28, 0.25f);
                var srgbReadback = ConsoleGraphicsDevice.RecordHostClearProbe(device, list, 29, 0, 0.25f);

                ulong marker = 0;

                var write = (delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, uint, void>)Slot(list,
                    newer ? 72 : 70);
                var waitValue =
                    (delegate* unmanaged[Stdcall]<nint, ulong, uint, ulong, void>)Slot(list, newer ? 74 : 72);

                write(list, (ulong)&marker, 0x1234, 0, 0);

                waitValue(list, (ulong)&marker, 3, 0x1234);

                ConsoleGraphicsDevice.VerifyBarrierRecording(list, newer);

                write(list, (ulong)&marker, 0x5678, 0, 0);

                var fenceAddress = ((delegate* unmanaged[Stdcall]<nint, ulong>)Slot(fence, 12))(fence);

                write(list, fenceAddress, 9, 0, 0);

                waitValue(list, fenceAddress, 3, 9);

                if (Volatile.Read(ref *(ulong*)(nuint)fenceAddress) != 0)
                {
                    throw new InvalidOperationException("Fence packet write executed while recording.");
                }

                if (marker != 0)
                {
                    throw new InvalidOperationException("A command memory write executed while recording.");
                }

                var close = (delegate* unmanaged[Stdcall]<nint, int>)Slot(list, 9);

                Check(close(list), "Close command list");

                var execute = (delegate* unmanaged[Stdcall]<nint, uint, nint*, void>)Slot(queue, 10);

                execute(queue, 1, &list);

                if (marker != 0x5678)
                {
                    throw new InvalidOperationException("Segmented command write/wait order failed.");
                }

                writer.WriteLine("[graphics-probe] GPU fence-ordered write/wait/write passed.");

                writer.WriteLine(
                    $"[graphics-probe] {(newer ? "Legacy and enhanced" : "Legacy")} barrier recording and segmented submission passed (ordering only).");

                var signal = (delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(queue, newer ? 16 : 14);
                var complete = (delegate* unmanaged[Stdcall]<nint, ulong>)Slot(fence, 8);

                if (Volatile.Read(ref *(ulong*)(nuint)fenceAddress) != 9 || complete(fence) != 9)
                {
                    throw new InvalidOperationException(
                        "Fence packet write/wait did not update both completion domains.");
                }

                writer.WriteLine("[graphics-probe] Segmented fence-address write/wait passed.");

                var setEvent = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(fence, 9);

                using var completion = new EventWaitHandle(false, EventResetMode.AutoReset);

                Check(setEvent(fence, 42, completion.SafeWaitHandle.DangerousGetHandle()), "Fence event registration");

                Check(signal(queue, fence, 42), "Queue signal");

                if (!completion.WaitOne(TimeSpan.FromSeconds(10)) || complete(fence) != 42)
                {
                    Environment.FailFast(
                        "Graphics device probe: fence did not complete; objects may still be in flight.");
                }

                ConsoleGraphicsDevice.VerifyHostClearProbe(clearReadback);
                ConsoleGraphicsDevice.VerifyHostClearProbe(linearReadback, 64, 1);
                ConsoleGraphicsDevice.VerifyHostClearProbe(srgbReadback, 137, 1);

                writer.WriteLine(
                    "[graphics-probe] Guest sRGB backing with linear and default sRGB RTVs passed GPU readback.");

                ConsoleGraphicsDevice.VerifyHostResourceAbi(device, queue, allocator);

                ConsoleGraphicsDevice.VerifyDescriptorTables(queue);

                Check(reset(allocator), "Allocator reset after completion");

                var resetList =
                    (delegate* unmanaged[Stdcall]<nint, nint, nint, uint, int>)Slot(list, newer ? 139 : 137);

                Check(resetList(list, allocator, 0, 0), "ResetCommandListX");

                Check(close(list), "Close reset list");

                writer.WriteLine(
                    $"Device ABI {(newer ? "newer" : "older")}: queue, allocator, frame clock, display enumeration, raster state, list synchronization, submission and fence passed.");

                Release(ref list);

                Release(ref allocator);

                Release(ref fence);

                ConsoleGraphicsDevice.VerifyFenceAddressProbeCleanup();

                Release(ref queue);

                Release(ref device);
            }

            return 0;
        }
        finally
        {
            Release(ref list);

            Release(ref allocator);

            Release(ref fence);

            Release(ref queue);

            Release(ref device);
            // The managed device and its unmanaged tables have process lifetime.
        }
    }

    private static void VerifyFrameAndDisplay(nint device)
    {
        var interval = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, int>)Slot(device, 78);
        var schedule = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, int>)Slot(device, 79);
        var wait = (delegate* unmanaged[Stdcall]<nint, uint, uint, nint, uint, ulong*, int>)Slot(device, 80);
        var stats = (delegate* unmanaged[Stdcall]<nint, ulong, uint, uint*, nint, int>)Slot(device, 81);

        Check(interval(device, 0, 5_000_000, 2, 0), "SetFrameInterval");

        Check(schedule(device, 0, 0, 0, 0), "ScheduleFrameEvent");

        ulong token = 0;

        if (wait(device, 0, 0, 0, 2, &token) != 1 || token == 0)
        {
            throw new InvalidOperationException("Frame timeout/token ABI check failed.");
        }

        var previous = token;

        Check(interval(device, 0, 2_000, 2, 0), "Set short frame interval");

        if (wait(device, 0, 200, 0, 0, &token) != 0 || token <= previous)
        {
            throw new InvalidOperationException("Frame wait/token ABI check failed.");
        }

        uint count = 7;

        Check(stats(device, token, 0, &count, 0), "Frame statistics");

        if (count != 0)
        {
            throw new InvalidOperationException("Unexpected frame statistics.");
        }

        Check(interval(device, 0, 0, 2, 0), "Restore default frame interval");

        var dxgiId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        var dxgi = (nint)0;
        var adapter = (nint)0;
        var output = (nint)0;

        try
        {
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(device, 0);

            Check(query(device, &dxgiId, &dxgi), "DXGI device query");

            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter), "GetAdapter");

            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7);

            Check(enumerate(adapter, 0, &output), "EnumOutputs");

            var modes = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, int>)Slot(output, 21);

            count = 0;

            Check(modes(output, 0, 0, &count, null), "Display mode count");

            var mode = stackalloc uint[8];

            if (count != 1)
            {
                throw new InvalidOperationException("Unexpected logical display count.");
            }

            Check(modes(output, 0, 0, &count, mode), "Display mode description");

            if (mode[0] != 3840 || mode[1] != 2160 || mode[2] != 60 || mode[3] != 1)
            {
                throw new InvalidOperationException("Unexpected logical display mode.");
            }
        }
        finally
        {
            Release(ref output);

            Release(ref adapter);

            Release(ref dxgi);
        }
    }

    private static nint Slot(nint value, int index)
    {
        return (*(nint**)value)[index];
    }

    private static void Release(ref nint value)
    {
        if (value == 0)
        {
            return;
        }

        var release = (delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2);

        _ = release(value);

        value = 0;
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{operation}: HRESULT 0x{unchecked((uint)result):X8}.");
        }
    }
}
