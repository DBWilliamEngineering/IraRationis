// SPDX-License-Identifier: GPL-3.0-or-later

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
