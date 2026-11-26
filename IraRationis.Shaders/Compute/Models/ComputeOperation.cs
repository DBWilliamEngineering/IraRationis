// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Compute.Models;

public enum ComputeOperation
{
    Constant,
    Copy,
    Bitcast,
    Add,
    Subtract,
    Multiply,
    And,
    Or,
    Xor,
    LessThan,
    Equal,
    Select,
    Swizzle
}
