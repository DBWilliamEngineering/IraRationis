// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Loader.Instructions.Models;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ConsoleInstructionHandlerImageRange
{
    public readonly nuint Start;
    public readonly nuint End;

    public ConsoleInstructionHandlerImageRange(nuint start, nuint end)
    {
        Start = start;

        End = end;
    }
}
