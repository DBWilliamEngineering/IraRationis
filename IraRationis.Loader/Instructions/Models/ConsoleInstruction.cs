// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-loader/src/console_insn_decode.rs.

namespace IraRationis.Loader.Instructions.Models;

public readonly record struct ConsoleInstruction(
    ConsoleInstructionKind Kind,
    int Length,
    int Destination = 0,
    int? Source = null,
    bool RegisterField = false,
    byte FieldLength = 0,
    byte FieldIndex = 0);
