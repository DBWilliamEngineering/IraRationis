// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Media;
using IraRationis.Media.Playback;

namespace IraRationis.Cli.Commands;

internal sealed class ProbeGpuPresenter : IGpuVideoPresenter
{
    private readonly Nv12Presenter _presenter;

    public ProbeGpuPresenter(nint window, int width, int height, TextWriter report)
    {
        _presenter = new Nv12Presenter(window, width, height, report);
    }

    public bool Present(ReadOnlySpan<byte> nv12)
    {
        return _presenter.Present(nv12);
    }

    public void Dispose()
    {
        _presenter.Dispose();
    }
}
