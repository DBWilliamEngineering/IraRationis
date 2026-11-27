// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx, crates/victory-hle/src/modules/xmem.rs.

using Microsoft.Win32.SafeHandles;

namespace IraRationis.HLE.Memory.Models;

internal sealed unsafe record PhysicalPageSlice(SafeFileHandle Section, ulong Index);
