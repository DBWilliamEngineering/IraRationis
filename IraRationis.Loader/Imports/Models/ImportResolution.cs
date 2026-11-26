// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-loader/src/resolve.rs.


namespace IraRationis.Loader.Imports.Models;

public readonly record struct ImportResolution(nuint Address, ImportProvenance Provenance);
