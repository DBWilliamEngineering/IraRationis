// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Graphics;

public static partial class ConsoleGraphicsDevice
{
    // Observational only. Independent atomic reads are not a transactional snapshot.
    public static StartupGraphicsSnapshot CaptureStartupGraphics()
    {
        return new StartupGraphicsSnapshot(
            Interlocked.Read(ref _r24Draws),
            Interlocked.Read(ref _r24Dispatches),
            Interlocked.Read(ref _r24PresentAttempts),
            Interlocked.Read(ref _r24PresentSuccesses),
            Interlocked.Read(ref _r24FenceWaits),
            Interlocked.Read(ref _r24GuestWaits));
    }

    public readonly record struct StartupGraphicsSnapshot(
        long Draws,
        long Dispatches,
        long PresentAttempts,
        long PresentSuccesses,
        long FenceWaits,
        long GuestWaits);
}
