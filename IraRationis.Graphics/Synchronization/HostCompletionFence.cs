// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;

namespace IraRationis.Graphics.Synchronization;

// Exclusive leases: a timeline is returned only after every signal has completed.
// Never pool a guest fence or share an outstanding signal between queues.
internal sealed unsafe class HostCompletionFence : IDisposable
{
    private const int MaximumIdle = 16;
    private static readonly object PoolGate = new();
    private static readonly Stack<HostCompletionFenceFenceTimeline> Idle = new();
    private HostCompletionFenceFenceTimeline? _timeline;
    private bool _waiting;

    public HostCompletionFence()
    {
        var device = (nint)ConsoleGraphicsDevice._host;

        lock (PoolGate)
        {
            while (Idle.TryPop(out var timeline))
            {
                if (timeline.Device == device && timeline.Value < ulong.MaxValue - 1)
                {
                    _timeline = timeline;

                    Interlocked.Increment(ref ConsoleGraphicsDevice._fenceReuses);

                    return;
                }

                timeline.Dispose();
            }
        }

        _timeline = new HostCompletionFenceFenceTimeline(device);

        Interlocked.Increment(ref ConsoleGraphicsDevice._fenceCreations);
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
            if (Idle.Count < MaximumIdle && timeline.Device == (nint)ConsoleGraphicsDevice._host)
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
        var resultCode = ((delegate* unmanaged[Stdcall]<nint, nint, ulong, int>)ConsoleGraphicsDevice.Slot(queue, 14))(queue, timeline.Fence,
            value);

        if (resultCode < 0)
        {
            Environment.FailFast($"[graphics] Completion fence signal failed: 0x{resultCode:X8}.");
        }

        var getCompleted = (delegate* unmanaged[Stdcall]<nint, ulong>)ConsoleGraphicsDevice.Slot(timeline.Fence, 8);
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

            var setEvent = (delegate* unmanaged[Stdcall]<nint, ulong, nint, int>)ConsoleGraphicsDevice.Slot(timeline.Fence, 9);

            resultCode = setEvent(timeline.Fence, value, timeline.Completion.SafeWaitHandle.DangerousGetHandle());

            if (resultCode < 0)
            {
                Environment.FailFast($"[graphics] Completion fence event registration failed: 0x{resultCode:X8}.");
            }

            if (!timeline.Completion.WaitOne(ConsoleGraphicsDevice.MemoryWaitTimeoutMilliseconds))
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

        ConsoleGraphicsDevice.RecordFenceWait(Stopwatch.GetTimestamp() - started);
    }
}
