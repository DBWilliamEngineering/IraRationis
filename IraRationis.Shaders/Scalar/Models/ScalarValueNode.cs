// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Scalar.Models;

internal readonly record struct ScalarValueNode(ScalarValueKind Operation, int Left, int Right, uint Value);
