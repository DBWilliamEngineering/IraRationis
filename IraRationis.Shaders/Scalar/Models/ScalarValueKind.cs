// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Scalar.Models;

internal enum ScalarValueKind
{
    Input,
    Constant,
    Add,
    Subtract,
    And,
    Or,
    Xor,
    Less,
    NotEqual
}
