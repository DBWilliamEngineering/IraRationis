// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace IraRationis.HLE.Runtime.Services.Users.LocalUserService;

internal sealed unsafe partial class LocalUserService
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Duplicate(nint self, nint handle, nint* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        try
        {
            return FromObject(self)._users.Duplicate(handle, out *output);
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Close(nint self, nint handle)
    {
        FromObject(self)._users.Close(handle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Compare(nint self, nint first, nint second)
    {
        var users = FromObject(self)._users;

        return users.IsValid(first) && users.IsValid(second) ? 0 : first.CompareTo(second);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetMaximumUsers(nint self, uint* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 1;

        return RuntimeServiceResult.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetLocalId(nint self, nint handle, ulong* output)
    {
        var instance = FromObject(self);

        return instance.ReadIdentifier(handle, output, instance._users.Profile.LocalId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetUserId(nint self, nint handle, ulong* output)
    {
        var instance = FromObject(self);

        return instance.ReadIdentifier(handle, output, instance._users.Profile.UserId);
    }

    private int ReadIdentifier(nint handle, ulong* output, ulong value)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        if (!_users.IsValid(handle))
        {
            return RuntimeServiceResult.InvalidHandle;
        }

        *output = value;

        return RuntimeServiceResult.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindByLocalId(nint self, ulong identifier, nint* output)
    {
        var instance = FromObject(self);

        return instance.FindUser(identifier, instance._users.Profile.LocalId, output);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindByUserId(nint self, ulong identifier, nint* output)
    {
        var instance = FromObject(self);

        return instance.FindUser(identifier, instance._users.Profile.UserId, output);
    }

    private int FindUser(ulong identifier, ulong expected, nint* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        if (identifier == 0)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        try
        {
            return identifier == expected ? _users.Open(out *output) : RuntimeServiceResult.UserNotFound;
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIsGuest(nint self, nint handle, byte* output)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        return FromObject(self)._users.IsValid(handle)
            ? RuntimeServiceResult.Success
            : RuntimeServiceResult.InvalidHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetState(nint self, nint handle, uint* output)
    {
        return FromObject(self).ReadProfileValue(handle, output, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetAgeGroup(nint self, nint handle, uint* output)
    {
        return FromObject(self).ReadProfileValue(handle, output, 3);
    }

    private int ReadProfileValue(nint handle, uint* output, uint value)
    {
        if (output == null)
        {
            return RuntimeServiceResult.InvalidArgument;
        }

        *output = 0;

        if (!_users.IsValid(handle))
        {
            return RuntimeServiceResult.InvalidHandle;
        }

        *output = value;

        return RuntimeServiceResult.Success;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetGamertag(nint self, nint handle, uint component, nuint capacity, byte* buffer, nuint* used)
    {
        return ReadGamertag(self, handle, component, capacity, buffer, used);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetLegacyGamertag(nint self, nint handle, nuint capacity, byte* buffer, nuint* used)
    {
        return ReadGamertag(self, handle, 0, capacity, buffer, used);
    }

    private static int ReadGamertag(nint self, nint handle, uint component, nuint capacity, byte* buffer, nuint* used)
    {
        if (used != null)
        {
            *used = 0;
        }

        try
        {
            var instance = FromObject(self);

            if (!instance._users.IsValid(handle))
            {
                return RuntimeServiceResult.InvalidHandle;
            }

            if (component > 3)
            {
                return RuntimeServiceResult.InvalidArgument;
            }

            var text = component == 2 ? string.Empty : instance._users.Profile.Gamertag;
            var byteCount = Encoding.UTF8.GetByteCount(text);

            if (buffer == null || capacity < (nuint)byteCount + 1)
            {
                return RuntimeServiceResult.InsufficientBuffer;
            }

            Encoding.UTF8.GetBytes(text, new Span<byte>(buffer, byteCount));

            buffer[byteCount] = 0;

            if (used != null)
            {
                *used = (nuint)byteCount + 1;
            }

            instance._trace.Write("UserGamertag", $"component={component}; bytes={byteCount + 1}; offline=True");

            return RuntimeServiceResult.Success;
        }
        catch
        {
            return RuntimeServiceResult.Failure;
        }
    }
}
