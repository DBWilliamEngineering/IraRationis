// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Services.Async.Models;

internal enum XAsyncOperation : uint
{
    Begin = 0,
    DoWork = 1,
    GetResult = 2,
    Cancel = 3,
    Cleanup = 4
}
