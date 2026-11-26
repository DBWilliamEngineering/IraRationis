// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Presentation.Models;

internal sealed unsafe record GuestPreviewFrame(long Id, int Width, int Height, byte[] Pixels, int NonBlack, bool Present);
