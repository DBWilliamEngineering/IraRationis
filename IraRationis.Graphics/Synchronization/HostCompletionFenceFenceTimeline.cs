// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Synchronization;

internal sealed unsafe class HostCompletionFenceFenceTimeline : IDisposable
{
    public readonly EventWaitHandle Completion = new(false, EventResetMode.AutoReset);
    public readonly nint Device;
    public readonly nint Fence;
    public ulong Value;

    public HostCompletionFenceFenceTimeline(nint device)
    {
        Device = device;

        var iid = ConsoleGraphicsDevice.FenceId;

        nint fence = 0;

        var resultCode = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)ConsoleGraphicsDevice.Slot(device, 36))
            (device, 0, 0, &iid, &fence);

        if (resultCode < 0 || fence == 0)
        {
            Environment.FailFast($"[graphics] Completion fence creation failed: 0x{resultCode:X8}.");
        }

        Fence = fence;
    }

    public void Dispose()
    {
        Completion.Dispose();

        _ = ConsoleGraphicsDevice.HostRelease(Fence);
    }
}
