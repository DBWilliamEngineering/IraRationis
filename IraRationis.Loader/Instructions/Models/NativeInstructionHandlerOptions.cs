// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.Loader.Instructions.Models;

[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeInstructionHandlerOptions
{
    public readonly uint Size;
    public readonly uint Flags;

    public NativeInstructionHandlerOptions(InstructionEmulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Size = 8;
        Flags = (options.BatchClzero ? 1U : 0U)
                | (options.ProfileInstructions ? 2U : 0U)
                | (options.ResidentPageQueries ? 4U : 0U);
    }
}
