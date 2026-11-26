// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Playback;

/// <summary>Host-owned presentation contract; implementations must not retain borrowed frame bytes.</summary>
public interface IGpuVideoPresenter : IDisposable
{
    bool Present(ReadOnlySpan<byte> nv12);
}
