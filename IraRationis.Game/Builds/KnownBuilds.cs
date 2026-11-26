// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-game/src/lib.rs.

namespace IraRationis.Game.Builds;

public static class KnownBuilds
{
    // The upstream registry is intentionally empty at the reference revision.
    private static IReadOnlyList<KnownBuild> Builds
    {
        get;
    } = [];

    public static KnownBuild? Identify(string hash)
    {
        return Builds.FirstOrDefault(build => string.Equals(build.Blake3, hash, StringComparison.OrdinalIgnoreCase));
    }
}
