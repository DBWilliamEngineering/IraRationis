// SPDX-License-Identifier: GPL-3.0-or-later
// Guest PresentX ABI prefix and old/new queue slots cross-checked against
// LooperSalty/VIctory and fxceless/victory-fork, crates/victory-gpu/src/queue.rs.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.Graphics.Diagnostics.Models;
using IraRationis.Graphics.Presentation.Models;
using IraRationis.Graphics.Resources.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

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
        return TryReadMappingRecord((nuint)address, out plane);
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
        using var stageTiming = BeginTiming(GraphicsStage.PresentX);

        if (!LiveObjects.TryGetValue(self, out var table) || (table != _queueOld && table != _queueNew))
        {
            return InvalidArgument;
        }

        var request = Interlocked.Increment(ref _presentRequests);
        var resultCode = DecodePresentSource(planes, planeParameters, out var plane, out var resource);

        if (resultCode < 0 || (resource != 0 && !IsResource(resource)))
        {
            Console.Error.WriteLine(
                $"[graphics] PresentX #{request}: invalid plane prefix or resource; planes={planes}.");

            return InvalidArgument;
        }

        var sample = request <= 4 || (request & (request - 1)) == 0;
        var queue = Host(self);

        if (queue != 0 && *(uint*)(self + 0x10) == 0)
        {
            ConfigureWindow();
        }

        if (sample)
        {
            Console.Error.WriteLine(
                $"[graphics] IRX-GFX-PRESENT-R16: request={request}, queueType={*(uint*)(self + 0x10)}, planes={planes}, token={plane.Token}, resources={plane.ResourceCount}.");

            if (resource != 0)
            {
                var source = (ResourceObject*)resource;
                var desc = source->Description;

                Console.Error.WriteLine(
                    $"[graphics] PresentX source: size={desc.Width}x{desc.Height}, format={desc.Format}, layout=0x{desc.Layout:X}, hostAllocated={source->HostResource != 0}, knownSameQueueWriter={queue != 0 && GetResourceWriterQueue(resource) == queue}.");
            }
        }

        // Only the single-plane prefix is established. Do not pretend a multi-plane
        // video composite, a CPU-only allocation or an unknown GPU writer is a frame.
        if (planes == 1 && plane.ResourceCount == 1 && resource != 0 && *(uint*)(self + 0x10) == 0 &&
            (TryPresentSwapChain(queue, resource) || TryPreviewTextureResource(queue, resource, true)))
        {
            return 0;
        }

        if (!AllowHeadlessPresent)
        {
            if (sample)
            {
                Console.Error.WriteLine(
                    "[graphics] IRX-GFX-PRESENT-R16: REFUSED: single-plane same-queue GPU-written color source and an available presenter required.");
            }

            return NotImplemented;
        }

        var acknowledged = Interlocked.Increment(ref _headlessPresentRequests);

        if (sample)
        {
            Console.Error.WriteLine(
                $"[graphics] PresentX headless acknowledgement #{acknowledged}; displayed=0 (explicit diagnostic mode).");
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

        if (sizeof(SwapDescription) != 48 || sizeof(PresentationMonitorInfo) != 40 || sizeof(PresentationRect) != 16)
        {
            throw new InvalidOperationException("R19 DXGI swap-chain description ABI mismatch.");
        }

        Console.Error.WriteLine(
            $"[graphics] IRX-GFX-PRESENT-R19: swapchain={EnableSwapChain}, diagnosticPreview={EnableGuestPreviewWindow}, colorFlow={EnableColorFlow}.");

        Console.Error.WriteLine(
            $"[graphics] IRX-GFX-WINDOW-R19.1: enabled={EnableWindowed1080}; requestedClient={WindowClientWidth}x{WindowClientHeight}; set IRX_PRESENT_WINDOWED_1080=0 to preserve title window size.");

        Console.Error.WriteLine(
            $"[performance] IRX-R24: bounded completion-fence pooling and metrics active; verboseGraphics={EnableVerboseGraphics}.");

        Console.Error.WriteLine(
            "[graphics-probe] R16 PresentX old/new registration and plane prefix passed (CPU only).");
    }
}
