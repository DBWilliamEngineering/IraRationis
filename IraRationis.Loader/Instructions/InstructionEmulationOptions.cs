// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Loader.Instructions;

public sealed record InstructionEmulationOptions(
    bool BatchClzero = false,
    bool ProfileInstructions = false,
    bool ResidentPageQueries = false)
{
    public static InstructionEmulationOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;

        return new InstructionEmulationOptions(
            readVariable("IRX_VEH_BATCH_CLZERO") == "1",
            readVariable("IRX_VEH_PROFILE") == "1",
            readVariable("IRX_VEH_RESIDENT_PAGE_QUERY") == "1");
    }
}
