// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    // One private fence timeline per segmented list submission, never shared with guest fences.
    // All waits complete before disposal. The final segment retains the normal guest lifecycle.
    private sealed class HostCompletionFence : IDisposable
    {
        private readonly EventWaitHandle _completion = new(false, EventResetMode.AutoReset);
        private readonly nint _fence;
        private ulong _value;

        public HostCompletionFence()
        {
            var iid = FenceId;
            var device = (nint)_host;
            nint fence = 0;
            var hr = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device, 36))
                (device, 0, 0, &iid, &fence);
            if (hr < 0 || fence == 0)
            {
                Environment.FailFast($"[graphics] Completion fence creation failed: 0x{hr:X8}.");
            }

            _fence = fence;
        }

        public void Dispose()
        {
            _completion.Dispose();
            _ = HostRelease(_fence);
        }

        public void Wait(nint queue)
        {
            var value = ++_value;
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(queue, 14))(queue, _fence, value);
            if (hr < 0)
            {
                Environment.FailFast($"[graphics] Completion fence signal failed: 0x{hr:X8}.");
            }

            var getCompleted = (delegate* unmanaged[Stdcall]<nint, ulong>)Slot(_fence, 8);
            var completed = getCompleted(_fence);
            if (completed == ulong.MaxValue)
            {
                Environment.FailFast("[graphics] GPU removed while waiting for command memory writes.");
            }

            if (completed >= value)
            {
                return;
            }

            var setEvent = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(_fence, 9);
            hr = setEvent(_fence, value, _completion.SafeWaitHandle.DangerousGetHandle());
            if (hr < 0)
            {
                Environment.FailFast($"[graphics] Completion fence event registration failed: 0x{hr:X8}.");
            }

            if (!_completion.WaitOne(MemoryWaitTimeoutMilliseconds))
            {
                Environment.FailFast("[graphics] GPU completion timed out before deferred memory publication.");
            }

            completed = getCompleted(_fence);
            if (completed == ulong.MaxValue || completed < value)
            {
                Environment.FailFast($"[graphics] Invalid GPU completion: expected={value}, actual={completed}.");
            }
        }
    }
}
