// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media;

/// <summary>Single-owner, fixed-rate host playback accounting. Never resubmits a frame.</summary>
internal sealed class HostFrameTimeline
{
    private readonly int _count;
    private bool _finished;
    private int _last = -1;
    private bool _pending;

    internal HostFrameTimeline(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _count = count;
    }

    internal int Presented
    {
        get;
        private set;
    }

    internal int Skipped
    {
        get;
        private set;
    }

    internal double Position
    {
        get;
        private set;
    }

    internal bool TrySelect(double audioSeconds, out int frame)
    {
        if (_pending || _finished)
        {
            throw new InvalidOperationException("Cannot select a frame in the current timeline state.");
        }

        if (!double.IsFinite(audioSeconds) || audioSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioSeconds));
        }

        Position = Math.Max(Position, audioSeconds);
        frame = (int)Math.Min(_count - 1, Math.Floor(Position * 30));
        if (frame <= _last)
        {
            return false;
        }

        Skipped += frame - _last - 1;
        _last = frame;
        _pending = true;

        return true;
    }

    internal void Record(bool accepted)
    {
        if (!_pending)
        {
            throw new InvalidOperationException("No selected frame to record.");
        }

        _pending = false;
        if (accepted)
        {
            Presented++;
        }
        else
        {
            Skipped++;
        }
    }

    internal void Finish()
    {
        if (_pending || _finished)
        {
            throw new InvalidOperationException("Cannot finish the current timeline.");
        }

        Skipped += _count - _last - 1;
        _finished = true;
    }
}
