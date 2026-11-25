// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace IraRationis.Graphics;

public static unsafe partial class ConsoleGraphicsDevice
{
    // Exclusive leases: a timeline is returned only after every signal has completed.
    // Never pool a guest fence or share an outstanding signal between queues.
    private sealed class HostCompletionFence : IDisposable
    {
        private const int MaximumIdle = 16;
        private static readonly object PoolGate = new();
        private static readonly Stack<FenceTimeline> Idle = new();
        private FenceTimeline? _timeline;
        private bool _waiting;

        public HostCompletionFence()
        {
            var device = (nint)_host;
            lock (PoolGate)
            {
                while (Idle.TryPop(out var timeline))
                {
                    if (timeline.Device == device && timeline.Value < ulong.MaxValue - 1)
                    {
                        _timeline = timeline;
                        Interlocked.Increment(ref _r24FenceReuses);

                        return;
                    }

                    timeline.Dispose();
                }
            }

            _timeline = new FenceTimeline(device);
            Interlocked.Increment(ref _r24FenceCreations);
        }

        public void Dispose()
        {
            var timeline = Interlocked.Exchange(ref _timeline, null);
            if (timeline == null)
            {
                return;
            }

            if (_waiting)
            {
                Environment.FailFast("[graphics] R24 attempted to return an in-flight completion fence.");
            }

            lock (PoolGate)
            {
                if (Idle.Count < MaximumIdle && timeline.Device == (nint)_host)
                {
                    Idle.Push(timeline);

                    return;
                }
            }

            timeline.Dispose();
        }

        public void Wait(nint queue)
        {
            var timeline = _timeline ?? throw new ObjectDisposedException(nameof(HostCompletionFence));
            if (_waiting || timeline.Value >= ulong.MaxValue - 1)
            {
                Environment.FailFast("[graphics] R24 invalid completion fence lease or exhausted timeline.");
            }

            _waiting = true;
            var started = Stopwatch.GetTimestamp();
            var value = ++timeline.Value;
            var hr = ((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)Slot(queue, 14))(queue, timeline.Fence,
                value);
            if (hr < 0)
            {
                Environment.FailFast($"[graphics] Completion fence signal failed: 0x{hr:X8}.");
            }

            var getCompleted = (delegate* unmanaged[Stdcall]<nint, ulong>)Slot(timeline.Fence, 8);
            var completed = getCompleted(timeline.Fence);
            if (completed == ulong.MaxValue)
            {
                Environment.FailFast("[graphics] GPU removed while waiting for command memory writes.");
            }

            if (completed < value)
            {
                // A previously leased timeline has no outstanding registration.
                // Reset before registering this signal's event, never after.
                timeline.Completion.Reset();
                var setEvent = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)Slot(timeline.Fence, 9);
                hr = setEvent(timeline.Fence, value, timeline.Completion.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    Environment.FailFast($"[graphics] Completion fence event registration failed: 0x{hr:X8}.");
                }

                if (!timeline.Completion.WaitOne(MemoryWaitTimeoutMilliseconds))
                {
                    Environment.FailFast("[graphics] GPU completion timed out before deferred memory publication.");
                }

                completed = getCompleted(timeline.Fence);
                if (completed == ulong.MaxValue || completed < value)
                {
                    Environment.FailFast($"[graphics] Invalid GPU completion: expected={value}, actual={completed}.");
                }
            }

            _waiting = false;
            RecordR24FenceWait(Stopwatch.GetTimestamp() - started);
        }

        private sealed class FenceTimeline : IDisposable
        {
            public readonly EventWaitHandle Completion = new(false, EventResetMode.AutoReset);
            public readonly nint Device;
            public readonly nint Fence;
            public ulong Value;

            public FenceTimeline(nint device)
            {
                Device = device;
                var iid = FenceId;
                nint fence = 0;
                var hr = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, Guid*, nint*, int>)Slot(device, 36))
                    (device, 0, 0, &iid, &fence);
                if (hr < 0 || fence == 0)
                {
                    Environment.FailFast($"[graphics] Completion fence creation failed: 0x{hr:X8}.");
                }

                Fence = fence;
            }

            public void Dispose()
            {
                Completion.Dispose();
                _ = HostRelease(Fence);
            }
        }
    }
}
