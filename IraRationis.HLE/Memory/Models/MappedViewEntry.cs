// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Memory.Models;

internal readonly record struct MappedViewEntry<T>(ulong Address, ulong Size, T? Value) where T : class;
