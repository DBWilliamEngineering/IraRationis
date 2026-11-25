// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-loader/src/resolve.rs.

using IraRationis.PE;

namespace IraRationis.Loader;

public enum ImportProvenance
{
    Hle,
    Host,
    Title
}

public readonly record struct ImportResolution(nuint Address, ImportProvenance Provenance);

public interface IImportResolver
{
    ImportResolution? Resolve(string dll, ImportSymbol symbol);
}
