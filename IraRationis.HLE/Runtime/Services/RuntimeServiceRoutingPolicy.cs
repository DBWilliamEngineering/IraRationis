// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Services;

internal static class RuntimeServiceRoutingPolicy
{
    internal static readonly Guid AsyncProvider = new("073b7dcb-1fcf-4030-94be-e3c9eb623428");
    private static readonly HashSet<(Guid Provider, Guid Interface)> HostInterfaces =
    [
        (AsyncProvider, AsyncProvider),
        (new("37e56907-2f10-41e8-b72f-36edb185331a"), new("bf2346b2-39af-4658-b5ea-44713c7e83b3")),
        (new("af406016-e850-4aa8-a88d-2f3dcb9dac7e"), new("0b9d556e-ab70-43f4-baa0-9999ac4cb040")),
        (new("e349bd1a-fc20-4e40-b99c-4178cc6b409f"), new("1861cf2e-e18b-4834-a9f5-b4a4e6efb4cf")),
        (new("03f0fe74-fdd9-4e5c-b630-f9339c47acc5"), new("35f07670-706e-4bfb-9476-090798c5ebf3")),
        (new("3e241977-6237-41e9-8559-102c2d9983f1"), new("d722b373-8c4d-4692-9e51-d6ad9b37aa7d"))
    ];

    internal static bool AllowsHost(Guid provider, Guid requestedInterface)
    {
        return HostInterfaces.Contains((provider, requestedInterface));
    }
}
