// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics.Diagnostics.Models;

public readonly record struct StartupGraphicsSnapshot(
    long Draws,
    long Dispatches,
    long PresentAttempts,
    long PresentSuccesses,
    long FenceWaits,
    long GuestWaits);
