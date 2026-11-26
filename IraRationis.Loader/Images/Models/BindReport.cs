// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/windows.rs.

using IraRationis.Loader.Imports.Models;

namespace IraRationis.Loader.Images.Models;

public sealed record BindReport(int Hle, int Host, int Title, IReadOnlyList<MissingImport> Missing);
