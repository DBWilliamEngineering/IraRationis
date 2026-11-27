// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IraRationis.Cli.Diagnostics.Instructions;

internal sealed class InstructionProbeAllocation : IDisposable
{
    internal InstructionProbeAllocation(nuint size, bool commit = true)
    {
        var allocationType = InstructionProbeNativeMethods.Reserve
                             | (commit ? InstructionProbeNativeMethods.Commit : 0U);
        var address = InstructionProbeNativeMethods.VirtualAlloc(0, size, allocationType,
            InstructionProbeNativeMethods.ReadWrite);

        if (address == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot allocate instruction probe memory.");
        }

        Address = (nuint)address;
    }

    internal nuint Address
    {
        get;
        private set;
    }

    public void Dispose()
    {
        if (Address == 0)
        {
            return;
        }

        if (InstructionProbeNativeMethods.VirtualFree((nint)Address, 0, InstructionProbeNativeMethods.Release) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot release instruction probe memory.");
        }

        Address = 0;
    }
}
