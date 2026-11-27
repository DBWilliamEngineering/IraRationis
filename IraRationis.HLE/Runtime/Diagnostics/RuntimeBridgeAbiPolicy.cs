// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;

namespace IraRationis.HLE.Runtime.Diagnostics;

internal sealed class RuntimeBridgeAbiPolicy
{
    internal const string VerifiedSha256 = "38D212D8FC4F2723E2D0CAD88A0A78144D0E0CED9BCCE4A83468E7E90C6E88DF";
    private readonly string _modulePath;

    internal RuntimeBridgeAbiPolicy(string modulePath, string fingerprint)
    {
        _modulePath = modulePath;
        Fingerprint = fingerprint;
    }

    internal string Fingerprint
    {
        get;
    }

    internal bool Verified
    {
        get
        {
            return Fingerprint.Equals(VerifiedSha256, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static RuntimeBridgeAbiPolicy Capture()
    {
        var path = Path.Combine(Environment.SystemDirectory, "xgameruntime.dll");

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (file.Length > 16 * 1024 * 1024)
            {
                return new RuntimeBridgeAbiPolicy(path, "outsideDiagnosticSizeLimit");
            }

            // O(file bytes), once before guest execution; callbacks do not hash files.
            return new RuntimeBridgeAbiPolicy(path, Convert.ToHexString(SHA256.HashData(file)));
        }
        catch
        {
            return new RuntimeBridgeAbiPolicy(path, "unavailable");
        }
    }

    internal bool CanObserve(string modulePath, nint module, string export, nint target)
    {
        var relativeAddress = ExportRva(export);

        return Verified && module != 0 && target != 0 && relativeAddress != 0
               && _modulePath.Equals(modulePath, StringComparison.OrdinalIgnoreCase)
               && (nuint)target >= (nuint)module && (nuint)target - (nuint)module == relativeAddress;
    }

    internal static nuint ExportRva(string export)
    {
        // These private x64 entry points were checked against host 10.0.26100.9656 and guest call sites.
        //TODO: Damon William William -> Revalidate the calling shapes and fingerprint before adding another host runtime build.
        return export switch
        {
            "InitializeApiImpl" => 0x17FB0,
            "InitializeApiImplEx" => 0x17FD0,
            "InitializeApiImplEx2" => 0x17FE0,
            "QueryApiImpl" => 0x185B0,
            _ => 0
        };
    }
}
