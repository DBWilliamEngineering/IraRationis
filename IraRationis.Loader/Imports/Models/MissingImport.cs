// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/thunks.rs.


namespace IraRationis.Loader.Imports.Models;

public sealed record MissingImport(string Dll, string Symbol, uint SlotRva);
