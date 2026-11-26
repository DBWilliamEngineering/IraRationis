// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Presentation;

internal sealed partial class WindowsVideoWindow
{
    internal nint Handle
    {
        get
        {
            return _window;
        }
    }
}
