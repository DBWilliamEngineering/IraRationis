// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using IraRationis.HLE.Interop;
using IraRationis.HLE.Runtime.Diagnostics.Models;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal static unsafe partial class RuntimeBridgeCallSite
{
    private const int MaximumFrames = 32;

    internal static string Capture(RuntimeBridgeImage[] images)
    {
        var errorState = NativeErrorState.Capture();

        try
        {
            if (!OperatingSystem.IsWindows() || images is null || images.Length == 0)
            {
                return "unavailable";
            }

            var returnAddresses = stackalloc nint[MaximumFrames];
            var frameCount = CaptureStackBackTrace(0, MaximumFrames, returnAddresses, null);

            // O(frameCount * imageCount); native unwinding is limited to 32 frames and can be incomplete.
            for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                var returnAddress = (nuint)returnAddresses[frameIndex];

                foreach (var image in images)
                {
                    if (returnAddress >= image.BaseAddress && returnAddress - image.BaseAddress < image.Size)
                    {
                        return $"{Path.GetFileName(image.Name)}+0x{returnAddress - image.BaseAddress:X}";
                    }
                }
            }

            return "unavailable";
        }
        catch
        {
            return "unavailable";
        }
        finally
        {
            errorState.Restore();
        }
    }

    [LibraryImport("ntdll.dll", EntryPoint = "RtlCaptureStackBackTrace")]
    private static partial ushort CaptureStackBackTrace(uint framesToSkip, uint framesToCapture,
        nint* returnAddresses, uint* backTraceHash);
}
