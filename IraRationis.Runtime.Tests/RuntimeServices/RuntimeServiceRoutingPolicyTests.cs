// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Runtime.Services;
using IraRationis.HLE.Runtime.Services.Users;
using IraRationis.HLE.Runtime.Services.Users.LocalUserService;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal static unsafe class RuntimeServiceRoutingPolicyTests
{
    internal static void Run()
    {
        Require(RuntimeServiceRoutingPolicy.AllowsHost(RuntimeServiceRoutingPolicy.AsyncProvider,
            RuntimeServiceRoutingPolicy.AsyncProvider), "Host async is explicitly allowed.");
        Require(!RuntimeServiceRoutingPolicy.AllowsHost(RuntimeServiceRoutingPolicy.AsyncProvider, Guid.Empty),
            "A known provider does not authorize an unknown interface.");
        Require(!RuntimeServiceRoutingPolicy.AllowsHost(LocalUserService.Provider, LocalUserService.Provider),
            "Local users never route to the host user service.");
        Require(!RuntimeServiceRoutingPolicy.AllowsHost(new Guid("704c3f58-e629-4cc2-b197-30511b996fe2"),
            new Guid("1bfff3af-f14a-40a3-8e35-9ada906593f9")), "Host saves cannot receive local user handles.");
        Require(!RuntimeServiceRoutingPolicy.AllowsHost(new Guid("0dd112ac-7c24-448c-b92b-3960fb5bd30c"),
            new Guid("b09d803c-2414-4a05-82c6-66dfdc9e9a44")), "Host store cannot receive local user handles.");
        Require(!RuntimeServiceRoutingPolicy.AllowsHost(TitleRuntimeService.Provider, TitleRuntimeService.Interface),
            "The title service with a user-taking launch method stays local.");
        Require(!RuntimeServiceRoutingPolicy.AllowsHost(Guid.Empty, Guid.Empty), "Unknown providers are rejected.");

        var service = new TitleRuntimeService(0x1234ABCD, new RuntimeServiceTrace(TextWriter.Null));

        Require(!service.TryQuery(Guid.Empty, out var unknown) && unknown == 0, "Unknown title interfaces are rejected.");
        Require(service.TryQuery(TitleRuntimeService.Interface, out var address), "The verified title interface is available.");

        var methods = *(nint**)address;
        uint titleId = 0;
        var getTitleId = (delegate* unmanaged[Stdcall]<nint, uint*, int>)methods[3];

        Require(getTitleId(address, &titleId) == 0 && titleId == 0x1234ABCD, "Guest title identity is preserved.");
        Require(getTitleId(address, null) == RuntimeServiceResult.InvalidArgument, "Title output is required.");

        var requested = Guid.Empty;
        nint result = 123;

        Require(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)methods[0])(address, &requested, &result)
            == RuntimeServiceResult.NoInterface && result == 0, "Unknown COM interfaces clear their outputs.");

        ((delegate* unmanaged[Stdcall]<nint, uint>)methods[2])(address);

        Console.WriteLine("Runtime service routing and local title identity contracts passed.");
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition)
        {
            throw new InvalidOperationException(contract);
        }
    }
}
