// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/lib.rs.

using IraRationis.HLE.Runtime.Diagnostics;
using IraRationis.Loader.Imports;
using IraRationis.Loader.Imports.Models;
using IraRationis.PE.Models;

namespace IraRationis.HLE.Runtime;

public sealed class HleResolver(HleRegistry registry, HostPolicy host, IHostFunctionObserver? observer = null)
    : IImportResolver
{
    public ImportResolution? Resolve(string dll, ImportSymbol symbol)
    {
        var address = registry.Lookup(dll, symbol.ToString());

        if (address.HasValue)
        {
            return new ImportResolution(address.Value, ImportProvenance.Hle);
        }

        address = host.Resolve(dll, symbol);

        if (address.HasValue)
        {
            return new ImportResolution(observer?.Observe(dll, symbol, address.Value) ?? address.Value,
                ImportProvenance.Host);
        }

        return null;
    }
}
