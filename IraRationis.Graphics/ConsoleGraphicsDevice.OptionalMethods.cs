// SPDX-License-Identifier: GPL-3.0-or-later
// Rejection contracts adapted from Zettrox/ztrx victory-gpu/src/device.rs and object.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int UnknownPipelineLegacySlot = 57;
    private const int UnknownPipelineModernSlot = 67;
    private const int DebugErrorFilterSlot = 69;
    private static long _unknownPipelineLegacyCalls;
    private static long _unknownPipelineModernCalls;

    private static void RegisterOptionalDeviceMethods(nint* device)
    {
        device[UnknownPipelineLegacySlot] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)&RejectUnknownPipelineLegacy;
        device[UnknownPipelineModernSlot] =
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)&RejectUnknownPipelineModern;
        device[DebugErrorFilterSlot] = (nint)(delegate* unmanaged[Stdcall]<int>)&IgnoreDebugErrorFilter;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectUnknownPipelineLegacy(nint self, nint description, Guid* iid, nint* output)
    {
        return RejectUnknownPipeline(UnknownPipelineLegacySlot, description, output, ref _unknownPipelineLegacyCalls);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectUnknownPipelineModern(nint self, nint description, Guid* iid, nint* output)
    {
        return RejectUnknownPipeline(UnknownPipelineModernSlot, description, output, ref _unknownPipelineModernCalls);
    }

    private static int RejectUnknownPipeline(int slot, nint description, nint* output, ref long counter)
    {
        if (output != null)
        {
            *output = 0;
        }

        var calls = Interlocked.Increment(ref counter);
        if (calls <= 4 || (calls & (calls - 1)) == 0)
        {
            // The description layout is unknown. Never dereference it speculatively.
            Console.WriteLine(
                $"[graphics] Optional pipeline slot 0x{slot * 8:X}: call={calls}, description=0x{(nuint)description:X}; returning E_NOTIMPL (0x80004001), no pipeline created.");
        }

        return NotImplemented;
    }

    // Upstream ignores this debug-only setting; the Windows x64 ABI permits unused arguments.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int IgnoreDebugErrorFilter()
    {
        return 0;
    }

    private static void ValidateOptionalDeviceRegistration(nint* device)
    {
        if (device[UnknownPipelineLegacySlot]
            != (nint)(delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)&RejectUnknownPipelineLegacy ||
            device[UnknownPipelineModernSlot]
            != (nint)(delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)&RejectUnknownPipelineModern ||
            device[DebugErrorFilterSlot] != (nint)(delegate* unmanaged[Stdcall]<int>)&IgnoreDebugErrorFilter)
        {
            throw new InvalidOperationException("Optional device ABI registration failed (0x1C8, 0x218 or 0x228).");
        }
    }

    private static string? OptionalDeviceStatus(string kind, int index)
    {
        if (kind != "device")
        {
            return null;
        }

        return index switch
        {
            UnknownPipelineLegacySlot or UnknownPipelineModernSlot => "registered-rejection-only",
            DebugErrorFilterSlot => "registered-debug-noop",
            _ => null
        };
    }

    private static void VerifyOptionalDeviceAbi(nint* device)
    {
        foreach (var slot in new[] { UnknownPipelineLegacySlot, UnknownPipelineModernSlot })
        {
            var reject = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)device[slot];
            var output = (nint)123;
            // Deliberately invalid description: rejecting the call must not read it.
            if (reject(0, 1, null, &output) != NotImplemented || output != 0 ||
                reject(0, 0, null, null) != NotImplemented)
            {
                throw new InvalidOperationException($"Optional pipeline rejection ABI failed at 0x{slot * 8:X}.");
            }
        }

        var ignore = (delegate* unmanaged[Stdcall]<int>)device[DebugErrorFilterSlot];
        if (ignore() != 0)
        {
            throw new InvalidOperationException("Debug error filter ABI failed.");
        }

        // These probes run before title execution and must not consume its initial log budget.
        Interlocked.Exchange(ref _unknownPipelineLegacyCalls, 0);
        Interlocked.Exchange(ref _unknownPipelineModernCalls, 0);
        Console.WriteLine("[graphics-probe] Optional device rejection and debug filter ABI passed.");
    }
}
