// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Compute.Models;

public readonly record struct ComputeTerminator(ComputeTransfer Transfer, int Target = -1,
    int Alternative = -1, int Condition = -1);
