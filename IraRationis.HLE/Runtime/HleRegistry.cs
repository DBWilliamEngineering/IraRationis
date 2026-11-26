// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/registry.rs.

using IraRationis.HLE.Runtime.Models;

namespace IraRationis.HLE.Runtime;

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
