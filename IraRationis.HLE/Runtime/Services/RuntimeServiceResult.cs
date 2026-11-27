// SPDX-License-Identifier: GPL-3.0-or-later

namespace IraRationis.HLE.Runtime.Services;

internal static class RuntimeServiceResult
{
    internal const int Success = 0;
    internal const int NoInterface = unchecked((int)0x80004002);
    internal const int InvalidArgument = unchecked((int)0x80070057);
    internal const int InvalidHandle = unchecked((int)0x80070006);
    internal const int NotImplemented = unchecked((int)0x80004001);
    internal const int Failure = unchecked((int)0x80004005);
    internal const int OutOfMemory = unchecked((int)0x8007000E);
    internal const int InsufficientBuffer = unchecked((int)0x8007007A);
    internal const int Pending = unchecked((int)0x8000000A);
    internal const int Aborted = unchecked((int)0x80004004);
    internal const int UserNotFound = unchecked((int)0x89245104);
}
