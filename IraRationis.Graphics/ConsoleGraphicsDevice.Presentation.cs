// SPDX-License-Identifier: GPL-3.0-or-later
// Guest PresentX ABI prefix and old/new queue slots cross-checked against
// LooperSalty/VIctory and fxceless/victory-fork, crates/victory-gpu/src/queue.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    private const int PresentOldSlot = 30;
    private const int PresentNewSlot = 32;
    private const uint MaximumDiagnosticPlanes = 64;
    private const uint MaximumDiagnosticResources = 64;
    private static readonly bool AllowHeadlessPresent =
        Environment.GetEnvironmentVariable("IRX_PRESENT_HEADLESS") == "1";
    private static long _presentRequests;
    private static long _headlessPresentRequests;

    private static bool TryReadPresentPlane(nint address, out PresentPlanePrefix plane)
    {
        plane = default;
        if (address == 0)
        {
            return false;
        }

        var value = default(PresentPlanePrefix);
        nuint transferred = 0;
        var bytes = (nuint)sizeof(PresentPlanePrefix);
        if (ReadProcessMemory(-1, (void*)address, &value, bytes, &transferred) == 0 || transferred != bytes)
        {
            return false;
        }

        plane = value;

        return true;
    }

    private static int DecodePresentSource(uint planes, nint planeParameters, out PresentPlanePrefix plane,
        out nint resource)
    {
        plane = default;
        resource = 0;
        if (planes == 0)
        {
            return 0;
        }

        if (planes > MaximumDiagnosticPlanes || !TryReadPresentPlane(planeParameters, out plane) ||
            plane.ResourceCount > MaximumDiagnosticResources)
        {
            return InvalidArgument;
        }

        if (plane.ResourceCount == 0)
        {
            return 0;
        }

        if (plane.Resources == 0 || !TryReadMemory((nuint)plane.Resources, sizeof(nint), out var value))
        {
            return InvalidArgument;
        }

        resource = (nint)(nuint)value;

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PresentX(nint self, uint planes, nint planeParameters, nint parameters)
    {
        if (!LiveObjects.TryGetValue(self, out var table) || (table != _queueOld && table != _queueNew))
        {
            return InvalidArgument;
        }

        var request = Interlocked.Increment(ref _presentRequests);
        var hr = DecodePresentSource(planes, planeParameters, out var plane, out var resource);
        if (hr < 0)
        {
            Console.Error.WriteLine(
                $"[graphics] PresentX #{request}: invalid plane prefix or resource array; planes={planes}, plane=0x{planeParameters:X}.");

            return hr;
        }

        // A single checked snapshot, not a GPU submission. The guest owns the
        // resource for the duration of this call; no pointer survives it.
        if (resource != 0 && !IsResource(resource))
        {
            Console.Error.WriteLine($"[graphics] PresentX #{request}: unknown guest resource=0x{resource:X}.");

            return InvalidArgument;
        }

        var sample = request <= 4 || (request & (request - 1)) == 0;
        if (sample)
        {
            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-PRESENT-R4 request={request}, queueType={*(uint*)(self + 0x10)}, planes={planes}, token={plane.Token}, resources={plane.ResourceCount}, parameters=0x{parameters:X}.");
            if (resource != 0)
            {
                var source = (ResourceObject*)resource;
                var description = source->Description;
                Console.Error.WriteLine(
                    $"[graphics] PresentX source: guest=0x{resource:X}, address=0x{source->Address:X}, dimension={description.Dimension}, size={description.Width}x{description.Height}, format={description.Format}, layout=0x{description.Layout:X}, flags=0x{description.Flags:X}, samples={description.SampleCount}, hostAllocated={source->HostResource != 0}; content authority unknown.");
            }
            else
            {
                Console.Error.WriteLine("[graphics] PresentX source: no first-plane resource.");
            }
        }

        // Never equate a host allocation with valid rendered pixels. We have
        // neither a production swap chain nor authoritative VUM content yet.
        // Explicit diagnostic mode acknowledges only the call, not a frame.
        if (!AllowHeadlessPresent)
        {
            if (sample)
            {
                Console.Error.WriteLine(
                    "[graphics] PresentX not displayed: host presentation/coherence is unimplemented (E_NOTIMPL). IRX_PRESENT_HEADLESS=1 enables diagnostic-only continuation.");
            }

            return NotImplemented;
        }

        var acknowledged = Interlocked.Increment(ref _headlessPresentRequests);
        if (sample)
        {
            Console.Error.WriteLine(
                $"[graphics] PresentX headless acknowledgement #{acknowledged}; displayed=0; no GPU copy, wait, or synthetic pixels.");
        }

        return 0;
    }

    private static void RegisterPresentationMethods()
    {
        var method = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)&PresentX;
        ((nint*)_queueOld)[PresentOldSlot] = method;
        ((nint*)_queueNew)[PresentNewSlot] = method;
    }

    private static void ValidatePresentationRegistration()
    {
        var method = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)&PresentX;
        if (sizeof(nint) != 8 || sizeof(PresentPlanePrefix) != 24 ||
            ((nint*)_queueOld)[PresentOldSlot] != method || ((nint*)_queueNew)[PresentNewSlot] != method)
        {
            throw new InvalidOperationException("PresentX ABI registration failed.");
        }

        nint expected = 0x12345000;
        var prefix = new PresentPlanePrefix { Token = 42, ResourceCount = 1, Resources = (nint)(&expected) };
        if (DecodePresentSource(1, (nint)(&prefix), out var decoded, out var resource) != 0 ||
            decoded.Token != 42 || decoded.ResourceCount != 1 || resource != expected ||
            DecodePresentSource(1, 0, out _, out _) != InvalidArgument ||
            DecodePresentSource(0, 0, out _, out resource) != 0 || resource != 0)
        {
            throw new InvalidOperationException("PresentX plane decoding probe failed.");
        }

        prefix.Resources = 0;
        if (DecodePresentSource(1, (nint)(&prefix), out _, out _) != InvalidArgument)
        {
            throw new InvalidOperationException("PresentX null resource array probe failed.");
        }

        var invoke = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)method;
        if (invoke(0, 0, 0, 0) != InvalidArgument)
        {
            throw new InvalidOperationException("PresentX invalid queue probe failed.");
        }

        Console.Error.WriteLine(
            "[graphics-probe] PresentX old/new registration and checked plane prefix passed (CPU only; no displayed frame).");
    }

    // Only this prefix is established. Do not infer the full plane stride,
    // placement, scaling, color space, flags, or optional parameter layout.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PresentPlanePrefix
    {
        [FieldOffset(0)] public ulong Token;
        [FieldOffset(8)] public uint ResourceCount;
        [FieldOffset(16)] public nint Resources;
    }
}
