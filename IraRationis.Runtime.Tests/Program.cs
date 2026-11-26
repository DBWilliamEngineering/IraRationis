// SPDX-License-Identifier: GPL-3.0-or-later
// Tests adapted from upstream console_insn_decode.rs, registry.rs and runtime.rs.

using System.Buffers.Binary;
using IraRationis.Graphics.Device;
using IraRationis.HLE.Input;
using IraRationis.HLE.Input.Models;
using IraRationis.HLE.Runtime;
using IraRationis.HLE.Runtime.Models;
using IraRationis.Loader.Imports.Models;
using IraRationis.Loader.Instructions;
using IraRationis.Loader.Instructions.Models;
using IraRationis.Loader.Threads;
using IraRationis.PE.Models;
using IraRationis.Runtime.Tests.Contracts;
using IraRationis.Runtime.Tests.Hardware;
using IraRationis.Runtime.Tests.Instructions;
using IraRationis.Runtime.Tests.Input;
using IraRationis.Runtime.Tests.Memory;
using IraRationis.Runtime.Tests.RuntimeBridge;

namespace IraRationis.Runtime.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args is ["--runtime-bridge-only"])
        {
            InputDiagnosticReportTests.Run();

            RuntimeBridgeDiagnosticsTests.Run();

            Console.WriteLine("Runtime bridge diagnostic contracts passed.");

            return 0;
        }

        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: IraRationis.Runtime.Tests [--runtime-bridge-only]");

            return 2;
        }

        TestInstructions();

        TestSse4A();

        TestTlsPatching();

        TestRuntime();

        TestHostPolicy();

        FoundationTests.Run();

        HardwareCapabilityTests.Run();

        GraphicsCapabilityTests.Run();

        InstructionEmulationOptionsTests.Run();

        InputDiagnosticReportTests.Run();

        RuntimeBridgeDiagnosticsTests.Run();

        MappedViewIndexTests.Run();

        PhysicalPageTests.Run();

        InteropTests.Run();

        GameInputModule.VerifyLayouts();

        Require(
            GameInputModule
                .ConvertPad(0x1014, 255, 0, short.MinValue, short.MaxValue, 0, 0)
            == new GameInputPadState(0x105, 1, 0, -1, 1, 0, 0),
            "GameInput pad conversion");

        Require(new HleRegistry([GameInputModule.Create()]).Lookup("GameInput.dll", "#1") is not null,
            "GameInput ordinal");

        Console.WriteLine("Runtime component tests passed.");

        return 0;
    }

    private static void TestInstructions()
    {
        Check([0x0F, 0x01, 0xFC], new ConsoleInstruction(ConsoleInstructionKind.Clzero, 3));

        Check([0x0F, 0x01, 0xFC, 0x48, 0x83, 0xC0, 0x40, 0x4C, 0x39, 0xD0, 0x72, 0xF4],
            new ConsoleInstruction(ConsoleInstructionKind.ClzeroLoop, 12, 10));

        Check([0x0F, 0x01, 0xFC, 0x48, 0x83, 0xC0, 0x40, 0x48, 0x39, 0xD0, 0x72, 0xF4],
            new ConsoleInstruction(ConsoleInstructionKind.ClzeroLoop, 12, 2));

        Check([0x67, 0x0F, 0x01, 0xFC, 0x48, 0x83, 0xC0, 0x40, 0x48, 0x39, 0xD0, 0x72, 0xF4],
            new ConsoleInstruction(ConsoleInstructionKind.Clzero, 4));

        Check([0x0F, 0x01, 0xFD], new ConsoleInstruction(ConsoleInstructionKind.Rdpru, 3));

        Check([0x0F, 0x01, 0xFA], new ConsoleInstruction(ConsoleInstructionKind.Monitorx, 3));

        Check([0x0F, 0x01, 0xFB], new ConsoleInstruction(ConsoleInstructionKind.Mwaitx, 3));

        Check([0x0F, 0x0B], new ConsoleInstruction(ConsoleInstructionKind.Ud2, 2));

        Check([0x66, 0x0F, 0xAE, 0x30], new ConsoleInstruction(ConsoleInstructionKind.Clwb, 4));

        Check([0x66, 0x0F, 0xAE, 0x71, 0x40], new ConsoleInstruction(ConsoleInstructionKind.Clwb, 5));

        Check([0x66, 0x0F, 0xAE, 0x74, 0x24, 0x08], new ConsoleInstruction(ConsoleInstructionKind.Clwb, 6));

        Check([0x66, 0x0F, 0xAE, 0x35, 1, 2, 3, 4], new ConsoleInstruction(ConsoleInstructionKind.Clwb, 8));

        Check([0x66, 0x41, 0x0F, 0xAE, 0x30], new ConsoleInstruction(ConsoleInstructionKind.Clwb, 5));

        Check([0xF3, 0x0F, 0xC7, 0xF8], new ConsoleInstruction(ConsoleInstructionKind.Rdpid, 4));

        Check([0xF3, 0x41, 0x0F, 0xC7, 0xF9], new ConsoleInstruction(ConsoleInstructionKind.Rdpid, 5, 9));

        Check([0xF2, 0x0F, 0x78, 0xC1, 0x08, 0x10],
            new ConsoleInstruction(ConsoleInstructionKind.Insertq, 6, 0, 1, false, 8, 16));

        Check([0xF2, 0x45, 0x0F, 0x79, 0xC1], new ConsoleInstruction(ConsoleInstructionKind.Insertq, 5, 8, 9, true));

        Check([0x66, 0x0F, 0x78, 0xC2, 0x04, 0x08],
            new ConsoleInstruction(ConsoleInstructionKind.Extrq, 6, 2, null, false, 4, 8));

        Check([0x66, 0x0F, 0x79, 0xCA], new ConsoleInstruction(ConsoleInstructionKind.Extrq, 4, 1, 2));

        Check([0xF2, 0x0F, 0x78, 0xC1, 0x08], null);

        Check([0x0F, 0x78, 0xC1, 0x08, 0x10], null);

        Check([0x66, 0x0F, 0xAE, 0x38], null);

        Check([0x0F, 0xAE, 0x30], null);

        Check([0x48, 0x8B, 0x01], null);

        Check([0x66, 0x0F, 0xAE, 0x74, 0x24], null);

        Check([0x0F], null);

        Check([0xF2, 0xF3, 0x0F, 0x78, 0xC1, 0x10, 0x10], null);

        Check([0x41, 0xF2, 0x0F, 0x78, 0xC1, 0x10, 0x10],
            new ConsoleInstruction(ConsoleInstructionKind.Insertq, 7, 0, 1, false, 16, 16));
    }

    private static void TestSse4A()
    {
        Require(ConsoleInstructionDecoder.ExtractQuadword(0x123456789ABCDEF0UL, 8, 16) == 0xBC, "EXTRQ");

        Require(ConsoleInstructionDecoder.ExtractQuadword(0x123456789ABCDEF0UL, 0, 0) == 0x123456789ABCDEF0UL,
            "EXTRQ 64");

        Require(ConsoleInstructionDecoder.ExtractQuadword(0x8000000000000000UL, 1, 63) == 1, "EXTRQ high bit");

        Require(ConsoleInstructionDecoder.InsertQuadword(ulong.MaxValue, 0xAB, 8, 8) == 0xFFFFFFFFFFFFABFFUL,
            "INSERTQ");

        Require(ConsoleInstructionDecoder.InsertQuadword(0, 0x1FF, 4, 60) == 0xF000000000000000UL,
            "INSERTQ high field");

        Require(ConsoleInstructionDecoder.InsertQuadword(0x1111, 0xDEADBEEF00000000UL, 0, 0) == 0xDEADBEEF00000000UL,
            "INSERTQ 64");
    }

    private static void TestTlsPatching()
    {
        var code = new byte[] { 0x65, 0x48, 0x8B, 0x04, 0x25, 0x58, 0, 0, 0, 0xC3 };
        var count = TlsRedirector.PatchLoads(code, 0x1000, 0x2000);

        Require(count == 1 && code[0] == 0xE8, "TLS patch opcode");

        Require(BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(1, 4)) == 0xFFB, "TLS call displacement");

        Require(code.AsSpan(5, 4).SequenceEqual(new byte[] { 0x0F, 0x1F, 0x40, 0 }), "TLS padding");

        var r12 = new byte[] { 0x65, 0x4C, 0x8B, 0x24, 0x25, 0x58, 0, 0, 0 };

        Require(TlsRedirector.PatchLoads(r12, 0x1000, 0x2000) == 1, "TLS r12 patch");

        Require(BinaryPrimitives.ReadInt32LittleEndian(r12.AsSpan(1, 4)) == 0x117B, "TLS r12 displacement");

        var rsp = new byte[] { 0x65, 0x48, 0x8B, 0x24, 0x25, 0x58, 0, 0, 0 };

        Require(TlsRedirector.PatchLoads(rsp, 0x1000, 0x2000) == 0, "Do not patch RSP");
    }

    private static void TestRuntime()
    {
        const string manifest =
            "\uFEFF<?xml version=\"1.0\"?><Game configVersion=\"1\"><!-- <TitleId>00000000</TitleId> --><Identity Name=\"x\"/><TitleId>639AA8DD</TitleId><![CDATA[<TitleId>1</TitleId>]]><StoreId>9&amp;C</StoreId><Other><SizeMB>7</SizeMB></Other><PersistentLocalStorage><SizeMB>1024</SizeMB></PersistentLocalStorage></Game>";

        var title = Title.FromManifest(manifest);

        Require(title.Id == 0x639AA8DD && title.StorageBytes == 1024UL << 20 && title.StoreId == "9&C", "Manifest");

        Require(Title.FromManifest(string.Empty) == Title.Empty, "Empty manifest");

        Require(ConsoleConfiguration.GetTitleMemory(ConsoleModel.SeriesX) == (10UL << 30, 7UL << 29),
            "Series X memory");

        Require(ConsoleConfiguration.GetTitleMemory(ConsoleModel.SeriesS) == (0UL, 7776UL << 20), "Series S memory");

        RuntimeState.SetMainImage(0x1000);

        RuntimeState.SetImagePath(0x1000, "test.exe");

        Require(RuntimeState.MainImage == 0x1000 && RuntimeState.FindImage("TEST.EXE") == 0x1000,
            "Runtime image state");

        RuntimeState.ForgetImage(0x1000);

        Require(RuntimeState.FindImage("TEST.EXE") is null, "Forget image");
    }

    private static void TestHostPolicy()
    {
        var registry = new HleRegistry([
            new HleModule("test", ["api-ms-win-core-libraryloader-*"], [new HleExport("Probe", 0x1234)])
        ]);
        var host = new HostPolicy();
        var resolver = new HleResolver(registry, new HostPolicy([]));

        Require(host.Allows("KERNEL32.DLL") && !host.Allows("xmem.dll"), "Host policy");

        Require(registry.Lookup("API-MS-WIN-CORE-LIBRARYLOADER-L1-2-0.DLL", "Probe") == 0x1234, "Registry wildcard");

        Require(registry.Lookup("API-MS-WIN-CORE-LIBRARYLOADER-L1-2-0.DLL", "probe") is null, "Export name case");

        Require(
            resolver.Resolve("api-ms-win-core-libraryloader-l1-2-0.dll", new ImportSymbol("Probe", 0))
            == new ImportResolution(0x1234, ImportProvenance.Hle), "HLE resolver");

        Require(resolver.Resolve("xmem.dll", new ImportSymbol("Unknown", 0)) is null, "Missing import");
    }

    private static void Check(ReadOnlySpan<byte> code, ConsoleInstruction? expected)
    {
        Require(ConsoleInstructionDecoder.Decode(code) == expected, $"Decode {Convert.ToHexString(code)}");
    }

    private static void Require(bool condition, string test)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Failed: {test}");
        }
    }
}
