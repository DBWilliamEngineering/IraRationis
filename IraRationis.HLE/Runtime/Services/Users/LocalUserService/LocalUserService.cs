// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Runtime.Services.Async;
using IraRationis.HLE.Runtime.Services.Async.Models;
using IraRationis.HLE.Runtime.Services.Users.Models;

namespace IraRationis.HLE.Runtime.Services.Users.LocalUserService;

internal sealed unsafe partial class LocalUserService
{
    internal static readonly Guid Provider = new("01acd177-91f9-4763-a38e-ccbb55ce32e0");
    internal static readonly Guid GamertagInterface = new("cef4fac0-7676-4a94-a119-4c43f9eb5b74");
    private readonly HostRuntimeAsyncService _async;
    private readonly RuntimeServiceObject _baseObject;
    private readonly RuntimeServiceObject _gamertagObject;
    private readonly Lock _registrationGate = new();
    private readonly Dictionary<ulong, LocalUserChangeRegistration> _registrations = [];
    private readonly RuntimeServiceTrace _trace;
    private readonly LocalUserDirectory _users;
    private ulong _nextRegistration;

    internal LocalUserService(HostRuntimeAsyncService asyncService, LocalUserProfile profile,
        RuntimeServiceTrace trace)
    {
        _async = asyncService;
        _users = new LocalUserDirectory(profile);
        _trace = trace;
        _baseObject = new RuntimeServiceObject(this, CreateBaseTable(), ResolveInterface, Provider);
        _gamertagObject = new RuntimeServiceObject(this,
            [0, 0, 0, (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nuint, byte*, nuint*, int>)&GetGamertag],
            ResolveInterface, GamertagInterface);
    }

    internal bool TryQuery(Guid requested, out nint address)
    {
        address = ResolveInterface(requested);

        return address != 0;
    }

    private nint ResolveInterface(Guid requested)
    {
        return requested == Provider || requested == RuntimeServiceObject.UnknownInterface ? _baseObject.Retain()
            : requested == GamertagInterface ? _gamertagObject.Retain() : 0;
    }

    private static LocalUserService FromObject(nint self)
    {
        return RuntimeServiceObject.Owner<LocalUserService>(self);
    }

    private static nint[] CreateBaseTable()
    {
        return
        [
            0, 0, 0,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&Duplicate,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, void>)&Close,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&Compare,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetMaximumUsers,
            (nint)(delegate* unmanaged[Stdcall]<nint, uint, XAsyncBlock*, int>)&AddAsync,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nint*, int>)&AddResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong*, int>)&GetLocalId,
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, nint*, int>)&FindByLocalId,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong*, int>)&GetUserId,
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, nint*, int>)&FindByUserId,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)&GetIsGuest,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)&GetState,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nuint, byte*, nuint*, int>)&GetLegacyGamertag,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, XAsyncBlock*, int>)&RejectGamerPicture,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint*, int>)&RejectResultSize,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint, nint, nuint*, int>)&RejectBufferResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)&GetAgeGroup,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, byte*, uint*, int>)&CheckPrivilege,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, uint, XAsyncBlock*, int>)&RejectPrivilegeResolution,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int>)&RejectResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, nuint, nint, nuint, nint, XAsyncBlock*,
                int>)&RejectToken,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint*, int>)&RejectResultSize,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint, nint, nint*, nuint*, int>)&RejectTokenResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint, nuint, nint, nuint, nint, XAsyncBlock*,
                int>)&RejectToken,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint*, int>)&RejectResultSize,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, nuint, nint, nint*, nuint*, int>)&RejectTokenResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, XAsyncBlock*, int>)&RejectIssueResolution,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int>)&RejectResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, XAsyncBlock*, int>)&RejectIssueResolution,
            (nint)(delegate* unmanaged[Stdcall]<nint, XAsyncBlock*, int>)&RejectResult,
            (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, nint, ulong*, int>)&RegisterChange,
            (nint)(delegate* unmanaged[Stdcall]<nint, ulong, byte, byte>)&UnregisterChange
        ];
    }
}
