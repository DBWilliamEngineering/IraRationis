// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-loader/src/resolve.rs.

using IraRationis.Loader.Imports.Models;
using IraRationis.PE.Models;

namespace IraRationis.Loader.Imports;

public interface IImportResolver
{
    ImportResolution? Resolve(string dll, ImportSymbol symbol);
}
