// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Graphics.Diagnostics.Models;

namespace IraRationis.Graphics.ConsoleGraphicsDevice;

public static partial class ConsoleGraphicsDevice
{
    // Observational only. Independent atomic reads are not a transactional snapshot.
    public static StartupGraphicsSnapshot CaptureStartupGraphics()
    {
        return new StartupGraphicsSnapshot(
            Interlocked.Read(ref _draws),
            Interlocked.Read(ref _dispatches),
            Interlocked.Read(ref _presentAttempts),
            Interlocked.Read(ref _presentSuccesses),
            Interlocked.Read(ref _fenceWaits),
            Interlocked.Read(ref _guestWaits));
    }
}
