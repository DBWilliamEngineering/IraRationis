// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-core/src/policy.rs.

namespace IraRationis.Core;

public static class AssetPolicy
{
    public const string RootMarker = ".irx-root";

    public static string EnsureOutsideRepository(string path)
    {
        var resolved = ResolvePath(path);
        var current = new DirectoryInfo(resolved);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, RootMarker))
                || File.Exists(Path.Combine(current.FullName, ".victory-root")))
            {
                throw new InvalidOperationException($"Game-derived data must remain outside the repository: {path}");
            }

            current = current.Parent;
        }

        return resolved;
    }

    private static string ResolvePath(string path)
    {
        var absolute = Path.GetFullPath(path);
        var root = Path.GetPathRoot(absolute)!;
        var relative = absolute[root.Length..];
        var parts = 
            relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 
                StringSplitOptions.RemoveEmptyEntries);

        var current = root;

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);

            var info = new DirectoryInfo(current);

            if (!Directory.Exists(current) || info.LinkTarget is null)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(true) 
                         ?? throw new IOException($"Cannot resolve {current}.");

            current = target.FullName;
        }

        return Path.GetFullPath(current);
    }
}
