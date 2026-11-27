// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.HLE.Runtime.Services;

namespace IraRationis.Runtime.Tests.RuntimeServices.Models;

internal sealed class AuthoredRuntimeAsyncOperation
{
    internal required nint Block
    {
        get;
        init;
    }

    internal required nint Context
    {
        get;
        init;
    }

    internal required nint Identity
    {
        get;
        init;
    }

    internal required nint Provider
    {
        get;
        init;
    }

    internal required nint CompletionCallback
    {
        get;
        init;
    }

    internal int Status
    {
        get;
        set;
    } = RuntimeServiceResult.Pending;

    internal nuint ResultSize
    {
        get;
        set;
    }

    internal bool CleanupDelivered
    {
        get;
        set;
    }

    internal bool CompletionDelivered
    {
        get;
        set;
    }

    internal bool ResultTaken
    {
        get;
        set;
    }
}
