// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Runtime.Diagnostics;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal static class RuntimeBridgeAbiPolicyTests
{
    internal static void Run()
    {
        const string modulePath = @"C:\Windows\System32\xgameruntime.dll";
        const string fingerprint = "38D212D8FC4F2723E2D0CAD88A0A78144D0E0CED9BCCE4A83468E7E90C6E88DF";
        var policy = new RuntimeBridgeAbiPolicy(modulePath, fingerprint);
        var unverified = new RuntimeBridgeAbiPolicy(modulePath, new string('0', 64));
        var missing = new RuntimeBridgeAbiPolicy(modulePath, "unavailable");
        nint module = 0x10000000;
        var exports = new (string Name, int Offset)[]
        {
            ("InitializeApiImpl", 0x17FB0),
            ("InitializeApiImplEx", 0x17FD0),
            ("InitializeApiImplEx2", 0x17FE0),
            ("QueryApiImpl", 0x185B0)
        };

        Require(policy.Verified && !unverified.Verified && !missing.Verified,
            "Private-runtime observation must require the explicitly verified fingerprint.");

        foreach (var export in exports)
        {
            var target = module + export.Offset;

            Require(policy.CanObserve(modulePath, module, export.Name, target),
                "The verified runtime and exact export RVA must permit the corresponding private observer.");
            Require(new RuntimeBridgeAbiPolicy(modulePath, fingerprint.ToLowerInvariant())
                    .CanObserve(modulePath.ToUpperInvariant(), module, export.Name, target),
                "Fingerprint and Windows path casing must not reject the same verified runtime.");
            Require(!unverified.CanObserve(modulePath, module, export.Name, target)
                    && !missing.CanObserve(modulePath, module, export.Name, target),
                "Unknown and unavailable runtime builds must retain their original exports.");
            Require(!policy.CanObserve(@"C:\authored\xgameruntime.dll", module, export.Name, target),
                "The same module name outside the captured System32 path must remain unwrapped.");
            Require(!policy.CanObserve(modulePath, module, export.Name, target + 1)
                    && !policy.CanObserve(modulePath, module, export.Name, module - 1),
                "An export with a mismatched or underflowing relative address must remain unwrapped.");
            Require(!policy.CanObserve(modulePath, 0, export.Name, target)
                    && !policy.CanObserve(modulePath, module, export.Name, 0),
                "Null module and export addresses must not satisfy private ABI verification.");
            Require(!policy.CanObserve(modulePath, module, export.Name.ToLowerInvariant(), target),
                "Private export names must retain case-sensitive symbol identity.");
        }

        foreach (var export in new[] { "XErrorReport", "UninitializeApiImpl", "XUserAddAsync", "Unknown" })
        {
            Require(!policy.CanObserve(modulePath, module, export, module + 0x17FB0),
                "Exports outside the verified private calling shapes must remain unwrapped.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
