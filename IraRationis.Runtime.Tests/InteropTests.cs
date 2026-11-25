// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using IraRationis.HLE;

namespace IraRationis.Runtime.Tests;

internal static unsafe class InteropTests
{
    internal static void Run()
    {
        Require(XMemModule.AddressRange(4) == (0UL, uint.MaxValue), "32-bit address range");
        Require(XMemModule.AddressRange(0) == (1UL << 32, (1UL << 40) - 1), "Default address range");
        var pointer = XMemModule.AllocateDefaultCore(4096, 8UL << 24);

        Require(pointer != null, "Default allocation");

        try
        {
            Require(((nuint)pointer & 255) == 0, "256-byte alignment");
            new Span<byte>(pointer, 4096).Fill(0xA5);
        }
        finally
        {
            XMemModule.FreeDefaultCore(pointer, 8UL << 24);
        }

        TestDsp();
        TestAcpHal();
        TestAcpControl();
        TestXApu();

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "irx-files-test"));
        FileSystemModule.Mount('G', root);
        Require(FileSystemModule.Translate(@"g:\foo\..\test.dat") == Path.Combine(root, "test.dat"),
            "Virtual drive normalization");
        Require(FileSystemModule.Translate(@"G:\..\test.dat") == Path.Combine(root, "test.dat"),
            "Virtual drive parent clamping");
        Require(FileSystemModule.Translate(@"Z:\other.dat") is null, "Unmapped drive");
    }

    private static void TestDsp()
    {
        var backing = stackalloc byte[0x10000];
        var dsp = (DspModule.Dsp*)null;

        Require(DspModule.ConnectCore(backing, 0x10000, 24, 1, 2, &dsp) == 0, "DSP connect");

        var description = new DspModule.StreamDescription { Kind = 1, Options = 2, Frames = 512, Channels = 2 };
        var status = (DspModule.Status*)null;
        var stream = (DspModule.Stream*)null;
        Require(DspModule.ActivateCore(dsp, &description, &status, &stream) == 0, "DSP activate");

        try
        {
            var samples = stackalloc float[1024];

            new Span<float>(samples, 1024).Fill(1);
            var command = new DspModule.Command { Output = samples };
            var ticket = 0U;
            Require(DspModule.SubmitCore(stream, &command, &ticket) == 0 && ticket == 1 && status->Completed == ticket,
                "DSP completion");

            foreach (var sample in new ReadOnlySpan<float>(samples, 1024))
            {
                Require(sample == 0, "Upstream DSP silent output");
            }
        }
        finally
        {
            Require(DspModule.DeactivateCore(stream) == 0, "DSP deactivate");
            Require(DspModule.DisconnectCore(dsp) == 0, "DSP disconnect");
        }
    }

    private static void TestAcpHal()
    {
        var registry = new HleRegistry([AcpHalModule.Create()]);

        nuint Export(string name)
        {
            return registry.Lookup("AcpHal.dll", name)
                   ?? throw new InvalidOperationException($"Missing AcpHal export {name}.");
        }

        var create = (delegate* unmanaged[Stdcall]<uint, uint, int>)Export("ApuCreateHeap");
        var allocate = (delegate* unmanaged[Stdcall]<void**, uint*, uint, uint, uint, int>)Export("ApuAlloc");
        var free = (delegate* unmanaged[Stdcall]<void*, void>)Export("ApuFree");
        var mapHost = (delegate* unmanaged[Stdcall]<void*, uint>)Export("ApuMapVirtualAddress");
        var mapDevice = (delegate* unmanaged[Stdcall]<uint, void*>)Export("ApuMapApuAddress");
        var valid = (delegate* unmanaged[Stdcall]<void*, uint, byte>)Export("ApuIsVirtualAddressValid");
        var allocateContexts = (delegate* unmanaged[Stdcall]<byte*, int>)Export("AcpHalAllocateShapeContexts");
        var releaseContexts = (delegate* unmanaged[Stdcall]<int>)Export("AcpHalReleaseShapeContexts");

        Require(create(0, 0) == unchecked((int)0x80070057), "APU invalid heap size");
        Require(create(0x100000, 0x10000) == 0, "APU create heap");
        Require(create(0x100000, 0) == unchecked((int)0x8ACA0001), "APU duplicate heap");
        var a = (void*)null;
        var b = (void*)null;
        var c = (void*)null;
        var first = 0U;
        var second = 0U;
        var third = 0U;
        try
        {
            Require(allocate(&a, &first, 0x1000, 6, 0) == unchecked((int)0x80070057) && a == null && first == 0,
                "APU invalid alignment");
            Require(allocate(&a, &first, 0x1000, 0x1000, 1) == 0 && a != null, "APU first allocation");
            Require(allocate(&b, &second, 0x1000, 0x1000, 0) == 0 && b != null, "APU second allocation");
            Require(first != 0 && second != 0 && first != second && (first & 0xFFF) == 0 && ((nuint)a & 0xFFF) == 0,
                "APU non-overlap and alignment");
            Require(new ReadOnlySpan<byte>(a, 0x1000).IndexOfAnyExcept((byte)0) < 0, "APU zero initialization");
            Require(mapHost((byte*)a + 0x44) == first + 0x44 && mapDevice(first + 0x44) == (byte*)a + 0x44,
                "APU interior bidirectional translation");
            Require(valid(a, 0x1000) == 1 && valid((byte*)a + 1, 0x1000) == 0 && valid(null, 0) == 0,
                "APU alignment validation");
            Require(mapHost((byte*)a + 0xFFF) == first + 0xFFF && mapDevice(0) == null, "APU range boundary");
            var oldHost = a;
            free(a);
            a = null;
            Require(mapHost(oldHost) == 0 && mapDevice(first) == null, "APU invalidation on free");
            Require(allocate(&c, &third, 0x800, 0x800, 1) == 0 && third == first, "APU free gap reuse");
            free(c);
            c = null;
            free(b);
            b = null;
            Require(allocate(&c, &third, 0x2000, 0x1000, 1) == 0 && third == first, "APU adjacent gap coalescing");
        }
        finally
        {
            free(a);
            free(b);
            free(c);
        }

        var description = stackalloc byte[96];
        new Span<byte>(description, 96).Clear();
        ((uint*)description)[4] = 2; // XMA: two 64-byte context records.
        Require(allocateContexts(description) == 0, "SHAPE context allocation");
        try
        {
            var host = (void*)Unsafe.ReadUnaligned<ulong>(description + 24 + 4 * 8);
            var device = Unsafe.ReadUnaligned<uint>(description + 72 + 4 * 4);
            Require(host != null && device != 0 && mapDevice(device + 127) == (byte*)host + 127, "SHAPE packed layout");
            Require(allocateContexts(description) < 0, "SHAPE duplicate allocation");
            free(host);
            Require(mapDevice(device) == host, "SHAPE ownership protected until release");
        }
        finally
        {
            Require(releaseContexts() == 0, "SHAPE release");
        }

        var releasedAddress = Unsafe.ReadUnaligned<uint>(description + 72 + 4 * 4);
        Require(mapDevice(releasedAddress) == null && releaseContexts() < 0, "SHAPE release invalidates mapping");
        Console.WriteLine(
            "[audio-probe] AcpHal exports, heap alignment, mapping, gap reuse and SHAPE storage passed (CPU only).");
    }

    private static void TestAcpControl()
    {
        var registry = new HleRegistry([AcpHalModule.Create()]);

        nuint Export(string name)
        {
            return registry.Lookup("AcpHal.dll", name)
                   ?? throw new InvalidOperationException($"Missing AcpHal export {name}.");
        }

        var create = (delegate* unmanaged[Stdcall]<nint*, int>)Export("AcpHalCreate");
        var allocate = (delegate* unmanaged[Stdcall]<void**, uint*, uint, uint, uint, int>)Export("ApuAlloc");
        var free = (delegate* unmanaged[Stdcall]<void*, void>)Export("ApuFree");
        var acp = (nint)0;
        var notification = (void*)null;
        var address = 0U;
        Require(create(null) == unchecked((int)0x80070057), "ACP null output");
        Require(create(&acp) == 0 && acp != 0, "ACP creation");
        var table = *(nint**)acp;
        var connect = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)table[0];
        var disconnect = (delegate* unmanaged[Stdcall]<nint, int>)table[1];
        var submit = (delegate* unmanaged[Stdcall]<nint, uint, ulong, uint, void*, uint, int>)table[2];
        var pop = (delegate* unmanaged[Stdcall]<nint, byte*, byte>)table[3];
        var count = (delegate* unmanaged[Stdcall]<nint, uint>)table[4];
        var release = (delegate* unmanaged[Stdcall]<nint, void>)table[5];
        try
        {
            var mask = 0x90U;
            Require(submit(acp, 2, 1, uint.MaxValue, &mask, 0) < 0, "ACP disconnected submission");
            Require(connect(acp, 0, 2) < 0, "ACP invalid command capacity");
            Require(connect(acp, 16, 2) == 0 && connect(acp, 16, 2) < 0, "ACP connect lifecycle");
            Require(allocate(&notification, &address, 5, 4, 0) == 0, "ACP notification allocation");
            Require(submit(acp, 2, 1, uint.MaxValue, &mask, address + 4) < 0 && count(acp) == 0,
                "ACP notification range rejection");
            Require(submit(acp, 2, 1, uint.MaxValue, &mask, address) == 0 && *(uint*)notification == 1,
                "ACP registration notification");
            *(uint*)notification = 0;
            Require(submit(acp, 5, 2, uint.MaxValue, &mask, address) == unchecked((int)0x80004001) &&
                    *(uint*)notification == 0 && count(acp) == 1, "ACP unsupported command must not complete");
            Require(submit(acp, 2, 3, uint.MaxValue, &mask, 0) == 0, "ACP second registration");
            Require(submit(acp, 2, 4, uint.MaxValue, &mask, 0) == 0 && count(acp) == 2, "ACP bounded queue");
            var message = stackalloc byte[28];
            Require(pop(acp, message) == 1 && *(uint*)message == 0x10 &&
                    Unsafe.ReadUnaligned<ulong>(message + 16) == 1, "ACP message ABI and FIFO");
            Require(submit(acp, 2, 5, uint.MaxValue, &mask, 0) == 0, "ACP ring wrap");
            Require(pop(acp, message) == 1 && Unsafe.ReadUnaligned<ulong>(message + 16) == 3,
                "ACP second message");
            Require(pop(acp, message) == 1 && *(uint*)(message + 4) == 1 &&
                    Unsafe.ReadUnaligned<ulong>(message + 16) == 5, "ACP dropped count");
            Require(pop(acp, message) == 0, "ACP empty queue");
            var unregister = 0x10U;
            Require(submit(acp, 3, 6, uint.MaxValue, &unregister, 0) == 0 && count(acp) == 0,
                "ACP unregister mask");
            Require(disconnect(acp) == 0 && pop(acp, message) == 1 && *(uint*)message == 0x80,
                "ACP disconnect message");
            Require(disconnect(acp) < 0, "ACP duplicate disconnect");
            Require(connect(acp, 1, 0) == 0 && disconnect(acp) == 0, "ACP zero capacity reconnect");
        }
        finally
        {
            free(notification);
            release(acp);
        }

        Console.WriteLine(
            "[audio-probe] ACP control ABI, lifecycle, bounded FIFO, notification bounds and unsupported command rejection passed.");
    }

    private static void TestXApu()
    {
        var registry = new HleRegistry([XApuModule.Create()]);

        nuint Export(string name)
        {
            return registry.Lookup("xapu.dll", name)
                   ?? throw new InvalidOperationException($"Missing XApu export {name}.");
        }

        var connect = (delegate* unmanaged[Stdcall]<uint*, nint*, nint*, int>)Export("XApuConnect");
        var disconnect = (delegate* unmanaged[Stdcall]<nint, int>)Export("XApuDisconnect");
        var enqueue = (delegate* unmanaged[Stdcall]<nint, byte*, uint*, int>)Export("XApuEnqueueCommand");
        var dequeue = (delegate* unmanaged[Stdcall]<nint, byte*, int>)Export("XApuDequeueResult");
        var count = (delegate* unmanaged[Stdcall]<nint, uint*, uint>)Export("XApuGetResultCount");
        var input = stackalloc uint[6] { 1, 2, 2, 65536, 0, 0 };
        var output = stackalloc nint[2];
        var handle = (nint)0;
        Require(connect(null, output, &handle) < 0 && handle == 0, "XApu null connect");
        input[0] = 2;
        Require(connect(input, output, &handle) == unchecked((int)0x80004001) && handle == 0,
            "XApu unsupported processing");
        input[0] = 1;
        Require(connect(input, output, &handle) == 0 && handle != 0 && output[0] != 0, "XApu connect");
        try
        {
            var memory = (byte*)output[0];
            Require(((nuint)memory & 15) == 0 && memory[0] == 0 && memory[65535] == 0, "XApu zeroed aligned backing");
            // Synthetic 440 Hz stereo, 20 ms Opus packet generated with FFmpeg/libopus.
            var packet = Convert.FromBase64String(
                "/LQGWqe14f0LvgB/+QdyhNj0C69bh6dmtpD7sail85yTi1ueGeRgTYRPqYtjhKqkjPtSY0+EOenwjqA8KAaZE8Hu/n/KnGus1EAeaHscF95RABf3LfAPgm/XglZ+p4zh+MLd5oSTnZDV8VmfNi3Y9o7JGpQxKW243UkC7OBBZ1C2zFaXpTO5pTQPCUw0kKlTr418VyfjNJP5pPBhmd7PeddQ5YuJhGRyz4OIgzM7S6hSVdEQ59J64P0Q59IjgxQ5t1eqRLbfL4r+WGH12MBXJflBkV3msKUTYhWG9DaLDBcKUF8sg6v87g4oduJtPaBqdLgT/d2QqeVb03I2Hq5m0z/iTKHpy+AuYKPZ7G2T/Tz4i7haUHF7D5DEiWzewH8hNtL9Uq2k/tVt");
            packet.AsSpan().CopyTo(new Span<byte>(memory, packet.Length));
            var command = stackalloc byte[64];
            var result = stackalloc byte[80];
            var commandBytes = new Span<byte>(command, 64);
            var resultBytes = new Span<byte>(result, 80);
            var total = 0U;
            Require(dequeue(handle, result) == unchecked((int)0x8AC81001), "XApu empty result");

            commandBytes.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[4..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[8..], 7);
            BinaryPrimitives.WriteUInt64LittleEndian(commandBytes[16..], 0x1234);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[24..], 2);
            BinaryPrimitives.WriteSingleLittleEndian(commandBytes[40..], 1);
            Require(enqueue(handle, command, &total) == 0 && total == 1, "XApu activate");

            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 3);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[8..], 8);
            commandBytes[24..].Clear();
            BinaryPrimitives.WriteSingleLittleEndian(commandBytes[28..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[36..], 960);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[40..], (uint)packet.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(commandBytes[44..], (nuint)memory);
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes[52..], 960 * 2 * sizeof(float));
            BinaryPrimitives.WriteUInt64LittleEndian(commandBytes[56..], (nuint)(memory + 4096));
            Require(enqueue(handle, command, &total) == 0 && total == 2, "XApu decode submit");
            Require(enqueue(handle, command, &total) == unchecked((int)0x8AC81000), "XApu per-stream queue full");
            Require(count(handle, &total) == 2 && total == 0, "XApu result count");
            Require(dequeue(handle, result) == 0 && BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == 0 &&
                    BinaryPrimitives.ReadUInt64LittleEndian(resultBytes[16..]) == 0x1234,
                "XApu activate result identity");
            Require(dequeue(handle, result) == 0 && BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(resultBytes[48..]) == 960 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(resultBytes[52..]) == 7680, "XApu decoded result shape");
            var energy = 0.0;
            foreach (var sample in new ReadOnlySpan<float>(memory + 4096, 1920))
            {
                Require(float.IsFinite(sample), "XApu finite PCM");
                energy += sample * sample;
            }

            Require(energy > 0.01, "XApu real non-silent Opus PCM");
            Require(count(handle, &total) == 0 && total == 2, "XApu dequeued total");

            BinaryPrimitives.WriteUInt64LittleEndian(commandBytes[56..], (nuint)(memory + 65535));
            Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0 &&
                    BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == unchecked((int)0x80070057),
                "XApu out-of-range output rejected");
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 5);
            Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0 &&
                    BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == 0, "XApu reset");
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 2);
            Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0, "XApu deactivate");
            BinaryPrimitives.WriteUInt32LittleEndian(commandBytes, 3);
            Require(enqueue(handle, command, null) == 0 && dequeue(handle, result) == 0 &&
                    BinaryPrimitives.ReadInt32LittleEndian(resultBytes[44..]) == unchecked((int)0x8AC81003),
                "XApu inactive stream");
        }
        finally
        {
            Require(disconnect(handle) == 0, "XApu disconnect");
        }

        Require(disconnect(handle) < 0 && count(handle, null) == 0, "XApu stale handle rejected");
        Console.WriteLine("[audio-probe] XApu ABI, bounded results, shared memory and non-silent Opus PCM passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
