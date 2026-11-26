// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal static unsafe partial class RuntimeBridgeContext
{
    internal static string Describe(string titlePath)
    {
        uint packageNameLength = 0;
        var packageStatus = GetCurrentPackageFullName(&packageNameLength, null);
        var titleDirectory = Path.GetDirectoryName(titlePath) ?? string.Empty;
        var hostManifest = File.Exists(Path.Combine(AppContext.BaseDirectory, "MicrosoftGame.config"));
        var titleManifest = File.Exists(Path.Combine(titleDirectory, "MicrosoftGame.config"));

        return $"hostExecutable={Environment.ProcessPath}; titleExecutable={titlePath}; "
               + $"hostManifestPresent={hostManifest}; titleManifestPresent={titleManifest}; "
               + $"packageIdentityStatus={packageStatus}; packageIdentityNameRead=False";
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetCurrentPackageFullName(uint* packageNameLength, char* packageName);
}
