// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Media;
using IraRationis.Media.Playback;

namespace IraRationis.Cli.Media;

internal sealed class RuntimeMediaPresenter : IGpuVideoPresenter
{
    private readonly Nv12Presenter _presenter;

    internal RuntimeMediaPresenter(nint window, int width, int height, MediaGpuContext context)
    {
        _presenter = new Nv12Presenter(window, width, height, Console.Out, context);
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
