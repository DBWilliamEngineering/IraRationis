// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.Loader.Instructions;
using IraRationis.Loader.Instructions.Models;

namespace IraRationis.Runtime.Tests.Instructions;

internal static class InstructionEmulationOptionsTests
{
    internal static void Run()
    {
        Require(InstructionEmulationOptions.FromEnvironment(_ => null) == new InstructionEmulationOptions(),
            "Instruction emulation options must default to disabled.");

        foreach (var disabledValue in new[] { "", "0", "true", "01", "1 ", " 1" })
        {
            Require(
                InstructionEmulationOptions.FromEnvironment(_ => disabledValue) == new InstructionEmulationOptions(),
                "Instruction emulation switches must require the exact value 1.");
        }

        for (var expectedFlags = 0U; expectedFlags < 8; expectedFlags++)
        {
            var variables = new Dictionary<string, string?>
            {
                ["IRX_VEH_BATCH_CLZERO"] = (expectedFlags & 1) != 0 ? "1" : null,
                ["IRX_VEH_PROFILE"] = (expectedFlags & 2) != 0 ? "1" : null,
                ["IRX_VEH_RESIDENT_PAGE_QUERY"] = (expectedFlags & 4) != 0 ? "1" : null
            };

            var options = InstructionEmulationOptions.FromEnvironment(name => variables[name]);
            var nativeOptions = new NativeInstructionHandlerOptions(options);

            Require(nativeOptions.Size == 8 && nativeOptions.Flags == expectedFlags,
                "Instruction emulation switches must remain independent in the native ABI.");
        }

        Require(Marshal.SizeOf<NativeInstructionHandlerOptions>() == 8
                && Marshal.OffsetOf<NativeInstructionHandlerOptions>(nameof(NativeInstructionHandlerOptions.Size)) == 0
                && Marshal.OffsetOf<NativeInstructionHandlerOptions>(nameof(NativeInstructionHandlerOptions.Flags))
                == 4,
            "Native instruction handler options must use the 8-byte ABI.");

        Require(Marshal.SizeOf<WritableCacheLineProbeResult>() == 32,
            "Writable cache-line probe results must use the 32-byte ABI.");

        var resultFields = new[]
        {
            nameof(WritableCacheLineProbeResult.Size),
            nameof(WritableCacheLineProbeResult.Cleared),
            nameof(WritableCacheLineProbeResult.ResidentPageQueries),
            nameof(WritableCacheLineProbeResult.ResidentPageHits),
            nameof(WritableCacheLineProbeResult.RegionQueries),
            nameof(WritableCacheLineProbeResult.AccessFaulted),
            nameof(WritableCacheLineProbeResult.ValidatedEnd)
        };

        for (var fieldIndex = 0; fieldIndex < resultFields.Length; fieldIndex++)
        {
            Require(Marshal.OffsetOf<WritableCacheLineProbeResult>(resultFields[fieldIndex]) == fieldIndex * 4,
                $"Unexpected native probe result offset: {resultFields[fieldIndex]}.");
        }

        Console.WriteLine("Instruction emulation options and native probe ABI contracts passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
