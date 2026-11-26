// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-pe/src/image.rs and layout.rs, Zettrox/ztrx.

namespace IraRationis.PE.Models;

public sealed record ImportModule(string Dll, IReadOnlyList<ImportEntry> Entries);
