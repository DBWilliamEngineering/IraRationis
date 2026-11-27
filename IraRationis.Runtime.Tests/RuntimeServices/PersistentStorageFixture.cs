// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal sealed class PersistentStorageFixture : IDisposable
{
    private readonly string _temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

    internal PersistentStorageFixture()
    {
        DirectoryPath = Path.Combine(_temporaryRoot, "irx-pls-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    internal string DirectoryPath
    {
        get;
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(DirectoryPath);

        if (!string.Equals(Path.GetDirectoryName(resolved), _temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("irx-pls-contract-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Persistent-storage fixture cleanup escaped its owned temporary directory.");
        }

        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, true);
        }
    }
}
