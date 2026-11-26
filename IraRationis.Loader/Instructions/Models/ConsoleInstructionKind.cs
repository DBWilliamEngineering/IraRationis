// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-loader/src/console_insn_decode.rs.

namespace IraRationis.Loader.Instructions.Models;

public enum ConsoleInstructionKind
{
    Clwb,
    Clzero,
    ClzeroLoop,
    Rdpru,
    Monitorx,
    Mwaitx,
    Rdpid,
    Extrq,
    Insertq,
    Ud2
}
