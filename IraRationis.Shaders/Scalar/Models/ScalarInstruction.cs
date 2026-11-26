// SPDX-License-Identifier: GPL-3.0-or-later
// GFX10 scalar opcodes referenced from DexNom/VIctory victory-shader/src/gfx10/opcodes.rs.


namespace IraRationis.Shaders.Scalar.Models;

public readonly record struct ScalarInstruction(
    int Offset,
    ScalarOperation Operation,
    int Destination,
    ScalarOperand Left,
    ScalarOperand Right);
