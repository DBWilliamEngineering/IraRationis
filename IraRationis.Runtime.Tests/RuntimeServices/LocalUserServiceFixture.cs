// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Runtime.Services;
using IraRationis.HLE.Runtime.Services.Async;
using IraRationis.HLE.Runtime.Services.Async.Models;
using IraRationis.HLE.Runtime.Services.Users;
using IraRationis.HLE.Runtime.Services.Users.Models;
using IraRationis.HLE.Runtime.Services.Users.LocalUserService;

namespace IraRationis.Runtime.Tests.RuntimeServices;

internal sealed unsafe class LocalUserServiceFixture : IDisposable
{
    internal static readonly LocalUserProfile Profile = new(0xFEDCBA9876543210UL, 0x9123456789ABCDEFUL, "Damon");

    internal LocalUserServiceFixture()
    {
        Host = new AuthoredRuntimeAsyncHost();
        Async = new HostRuntimeAsyncService(Host.Address);
        Service = new LocalUserService(Async, Profile, new RuntimeServiceTrace(TextWriter.Null));

        if (!Service.TryQuery(LocalUserService.Provider, out var userInterface) ||
            !Service.TryQuery(LocalUserService.GamertagInterface, out var gamertagInterface))
        {
            throw new InvalidOperationException("The authored user service did not expose its known interfaces.");
        }

        UserInterface = userInterface;
        GamertagInterface = gamertagInterface;
        AuthoredLocalUserCallbacks.Reset();
    }

    internal AuthoredRuntimeAsyncHost Host { get; }
    internal HostRuntimeAsyncService Async { get; }
    internal LocalUserService Service { get; }
    internal nint UserInterface { get; }
    internal nint GamertagInterface { get; }

    internal nint Method(int offset)
    {
        return (*(nint**)UserInterface)[offset / sizeof(nint)];
    }

    internal int Add(XAsyncBlock* block, uint options = 0)
    {
        return ((delegate* unmanaged[Stdcall]<nint, uint, XAsyncBlock*, int>)Method(0x38))(UserInterface, options, block);
    }

    internal int TakeResult(XAsyncBlock* block, nint* handle)
    {
        return ((delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint*, int>)Method(0x40))(UserInterface, block, handle);
    }

    internal void Close(nint handle)
    {
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Method(0x20))(UserInterface, handle);
    }

    public void Dispose()
    {
        Host.CancelPending();

        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)UserInterface)[2])(UserInterface);
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)GamertagInterface)[2])(GamertagInterface);

        Async.Dispose();
        Host.Dispose();
    }
}
