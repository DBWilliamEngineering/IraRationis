// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Shaders.Compute.Models;

public sealed record ComputeBlock(IReadOnlyList<ComputeInstruction> Instructions, ComputeTerminator Terminator);
