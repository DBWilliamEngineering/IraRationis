// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IraRationis.HLE.Runtime.Services.Async.Models;

namespace IraRationis.HLE.Runtime.Services.Users.LocalUserService;

internal sealed unsafe partial class LocalUserService
{
    private static int Unsupported(nint self, string operation)
    {
        FromObject(self)._trace.Write(operation, "hresult=0x80004001; offline=True; completionScheduled=False");

        return RuntimeServiceResult.NotImplemented;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CheckPrivilege(nint self, nint handle, uint options, uint privilege, byte* allowed, uint* reason)
    {
        if (allowed == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *allowed = 0;

        if (reason != null)
        {
            *reason = uint.MaxValue;
        }

        return FromObject(self)._users.IsValid(handle)
            ? Unsupported(self, "UserPrivilegeUnavailable")
            : RuntimeServiceResult.InvalidHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectGamerPicture(nint self, nint handle, uint size, XAsyncBlock* block)
    {
        return Unsupported(self, "UserGamerPictureUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectResultSize(nint self, XAsyncBlock* block, nuint* size)
    {
        if (size != null)
        {
            *size = 0;
        }

        return Unsupported(self, "UserResultSizeUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectBufferResult(nint self, XAsyncBlock* block, nuint capacity, nint buffer, nuint* used)
    {
        if (used != null)
        {
            *used = 0;
        }

        return Unsupported(self, "UserBufferResultUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectPrivilegeResolution(nint self, nint handle, uint options, uint privilege,
        XAsyncBlock* block)
    {
        return Unsupported(self, "UserPrivilegeResolutionUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectResult(nint self, XAsyncBlock* block)
    {
        return Unsupported(self, "UserResultUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectToken(nint self, nint handle, uint options, nint method, nint url,
        nuint headerCount, nint headers, nuint bodySize, nint body, XAsyncBlock* block)
    {
        return Unsupported(self, "UserTokenUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectTokenResult(nint self, XAsyncBlock* block, nuint capacity, nint buffer,
        nint* token, nuint* used)
    {
        if (token != null)
        {
            *token = 0;
        }

        if (used != null)
        {
            *used = 0;
        }

        return Unsupported(self, "UserTokenResultUnavailable");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RejectIssueResolution(nint self, nint handle, nint url, XAsyncBlock* block)
    {
        return Unsupported(self, "UserIssueResolutionUnavailable");
    }
}
