// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics;

internal sealed class GraphicsLogSampling
{
    private readonly long[] _listKindCounts = new long[8];
    private readonly bool _sampleGraphics;
    private readonly bool _sampleListKinds;
    private long _resourceLines;
    private long _resourceEmitted;
    private long _listLines;
    private long _listEmitted;

    internal GraphicsLogSampling(bool sampleGraphics, bool sampleListKinds, bool verbose)
    {
        _sampleGraphics = sampleGraphics;
        _sampleListKinds = sampleListKinds;
        Enabled = !verbose && (sampleGraphics || sampleListKinds);
    }

    internal bool Enabled { get; }

    internal bool ShouldEmitResourceView()
    {
        if (!Enabled)
        {
            return true;
        }

        var count = Interlocked.Increment(ref _resourceLines);
        if (_sampleGraphics && !IsSample(count))
        {
            return false;
        }

        Interlocked.Increment(ref _resourceEmitted);
        return true;
    }

    internal bool ShouldEmitCommandList(uint kind, out long sampledKindCount)
    {
        sampledKindCount = 0;
        if (!Enabled)
        {
            return true;
        }

        var count = Interlocked.Increment(ref _listLines);
        if (_sampleListKinds && kind < (uint)_listKindCounts.Length)
        {
            sampledKindCount = Interlocked.Increment(ref _listKindCounts[(int)kind]);
            if (!IsSample(sampledKindCount))
            {
                return false;
            }
        }
        else if (_sampleGraphics && !IsSample(count))
        {
            return false;
        }

        Interlocked.Increment(ref _listEmitted);
        return true;
    }

    internal string FormatSummary()
    {
        var resourceEmitted = Interlocked.Read(ref _resourceEmitted);
        var resourceLines = Interlocked.Read(ref _resourceLines);
        var listEmitted = Interlocked.Read(ref _listEmitted);
        var listLines = Interlocked.Read(ref _listLines);

        return FormattableString.Invariant(
            $"[performance] IRX-R50-LOG: resourceLines={resourceLines}; resourceEmitted={resourceEmitted}; listLines={listLines}; listEmitted={listEmitted}.");
    }

    internal static bool IsSample(long count)
    {
        return count <= 4 || (count & (count - 1)) == 0;
    }
}
