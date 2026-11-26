// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Media.Playback.Models;

internal readonly record struct StreamingMediaSessionVideoFrame(int Index, byte[] Buffer);
