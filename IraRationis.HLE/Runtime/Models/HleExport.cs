// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/registry.rs.

namespace IraRationis.HLE.Runtime.Models;

public sealed record HleExport(string Name, nuint Address);
