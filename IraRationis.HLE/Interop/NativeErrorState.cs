// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace IraRationis.HLE.Interop;

internal readonly struct NativeErrorState
{
    private NativeErrorState(int systemError, int pinvokeError)
    {
        SystemError = systemError;
        PInvokeError = pinvokeError;
    }

    internal int SystemError { get; }

    internal int PInvokeError { get; }

    internal static NativeErrorState Capture()
    {
        var systemError = Marshal.GetLastSystemError();
        var pinvokeError = Marshal.GetLastPInvokeError();

        return new NativeErrorState(systemError, pinvokeError);
    }

    internal void Restore()
    {
        Marshal.SetLastPInvokeError(PInvokeError);
        Marshal.SetLastSystemError(SystemError);
    }
}
