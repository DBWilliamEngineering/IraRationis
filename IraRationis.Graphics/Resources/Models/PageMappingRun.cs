// SPDX-License-Identifier: GPL-3.0-or-later
// Queue ABI and range kinds cross-checked against DexNom/VIctory, victory-gpu/src/queue.rs.


namespace IraRationis.Graphics.Resources.Models;

internal readonly record struct PageMappingRun(uint Kind, ulong Destination, ulong Source, ulong Bytes);
