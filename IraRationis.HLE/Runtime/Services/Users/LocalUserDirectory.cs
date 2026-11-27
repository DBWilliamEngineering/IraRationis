// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Runtime.Services.Users.Models;

namespace IraRationis.HLE.Runtime.Services.Users;

internal sealed class LocalUserDirectory
{
    private const int MaximumHandles = 4096;
    private readonly Lock _gate = new();
    private readonly HashSet<nint> _handles = [];
    private bool _added;
    private long _nextHandle;

    internal LocalUserDirectory(LocalUserProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
    }

    internal LocalUserProfile Profile
    {
        get;
    }

    internal bool IsAdded
    {
        get
        {
            lock (_gate)
            {
                return _added;
            }
        }
    }

    internal void AddUser()
    {
        lock (_gate)
        {
            _added = true;
        }
    }

    internal bool IsValid(nint handle)
    {
        lock (_gate)
        {
            return _handles.Contains(handle);
        }
    }

    internal int Open(out nint handle)
    {
        lock (_gate)
        {
            handle = 0;

            if (!_added)
            {
                return RuntimeServiceResult.UserNotFound;
            }

            if (_handles.Count >= MaximumHandles || _nextHandle == int.MaxValue)
            {
                return RuntimeServiceResult.OutOfMemory;
            }

            // O(1) expected lookup; monotonically issued tokens never alias a closed handle.
            handle = (nint)(++_nextHandle * 16 + 1);

            _handles.Add(handle);

            return RuntimeServiceResult.Success;
        }
    }

    internal int Duplicate(nint handle, out nint duplicate)
    {
        lock (_gate)
        {
            duplicate = 0;

            return _handles.Contains(handle) ? Open(out duplicate) : RuntimeServiceResult.InvalidHandle;
        }
    }

    internal void Close(nint handle)
    {
        lock (_gate)
        {
            _handles.Remove(handle);
        }
    }
}
