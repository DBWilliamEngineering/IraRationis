// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Compute.Models;

public readonly record struct ComputeInstruction(ComputeOperation Operation, int Destination,
    int Left = -1, int Right = -1, int Mask = -1, ComputeVector Literal = default);
