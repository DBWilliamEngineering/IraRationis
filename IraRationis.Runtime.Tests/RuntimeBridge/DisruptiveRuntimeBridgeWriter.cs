// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace IraRationis.Runtime.Tests.RuntimeBridge;

internal sealed class DisruptiveRuntimeBridgeWriter(bool throwOnWrite) : TextWriter
{
    internal int Writes
    {
        get;
        private set;
    }

    internal bool ThrowOnWrite
    {
        get;
        set;
    } = throwOnWrite;

    public override Encoding Encoding
    {
        get
        {
            return Encoding.UTF8;
        }
    }

    public override void WriteLine(string? value)
    {
        Writes++;
        Marshal.SetLastPInvokeError(809);
        Marshal.SetLastSystemError(919);

        if (ThrowOnWrite)
        {
            throw new IOException("Authored runtime bridge diagnostic sink failure.");
        }
    }
}
