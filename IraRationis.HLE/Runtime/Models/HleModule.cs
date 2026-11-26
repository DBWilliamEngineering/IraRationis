// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/registry.rs.

namespace IraRationis.HLE.Runtime.Models;

public sealed record HleModule(string Name, IReadOnlyList<string> Dlls, IReadOnlyList<HleExport> Exports)
{
    public bool Serves(string dll)
    {
        foreach (var pattern in Dlls)
        {
            if (pattern.EndsWith('*'))
            {
                if (dll.AsSpan().StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(dll, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
