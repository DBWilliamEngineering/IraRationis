// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Diagnostics;

public sealed record RuntimeBridgeOptions(bool Enabled = false, int PerOperationLimit = 128)
{
    public static RuntimeBridgeOptions FromEnvironment(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;

        return new RuntimeBridgeOptions(readVariable("IRX_RUNTIME_BRIDGE_TRACE") == "1");
    }
}
