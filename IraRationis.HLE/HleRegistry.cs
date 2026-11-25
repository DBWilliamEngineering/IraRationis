// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/registry.rs.

namespace IraRationis.HLE;

public sealed record HleExport(string Name, nuint Address);

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

public sealed class HleRegistry
{
    private readonly HleModule[] _modules;

    public HleRegistry(IEnumerable<HleModule> modules)
    {
        _modules = modules.ToArray();

        foreach (var module in _modules)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var export in module.Exports)
            {
                if (!names.Add(export.Name) || export.Address == 0)
                {
                    throw new ArgumentException($"Invalid export {module.Name}!{export.Name}.", nameof(modules));
                }
            }
        }
    }

    public nuint? Lookup(string dll, string symbol)
    {
        foreach (var module in _modules)
        {
            if (!module.Serves(dll))
            {
                continue;
            }

            foreach (var export in module.Exports)
            {
                if (string.Equals(export.Name, symbol, StringComparison.Ordinal))
                {
                    return export.Address;
                }
            }
        }

        return null;
    }
}
