// SPDX-License-Identifier: GPL-3.0-or-later

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
