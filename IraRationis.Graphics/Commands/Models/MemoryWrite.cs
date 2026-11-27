// SPDX-License-Identifier: GPL-3.0-or-later
// ABI and comparison conventions adapted from DexNom/VIctory victory-gpu/src/list.rs.


namespace IraRationis.Graphics.Commands.Models;

internal readonly unsafe record struct MemoryWrite(ulong Address, ulong Value, int Bytes, uint Flags);
