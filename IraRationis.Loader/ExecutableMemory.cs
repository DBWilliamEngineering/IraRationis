// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IraRationis.Loader;

internal static unsafe class ExecutableMemory
{
    internal static nint Allocate(nuint size, nint hint = 0)
    {
        var address = WindowsNative.VirtualAlloc(hint, size, WindowsNative.CommitReserve, WindowsNative.ReadWrite);

        if (address == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "VirtualAlloc failed.");
        }

        return address;
    }

    internal static void Seal(nint address, nuint size)
    {
        var oldProtection = 0U;

        WindowsNative.Check(WindowsNative.VirtualProtect(address, size, WindowsNative.ExecuteRead, &oldProtection),
            "Seal executable memory");

        WindowsNative.Check(WindowsNative.FlushInstructionCache(WindowsNative.GetCurrentProcess(), address, size),
            "Flush instruction cache");
    }

    internal static void Write(nint address, ReadOnlySpan<byte> data, bool executable)
    {
        var previous = 0U;
        var protection = executable ? WindowsNative.ExecuteReadWrite : WindowsNative.ReadWrite;

        WindowsNative.Check(WindowsNative.VirtualProtect(address, (nuint)data.Length, protection, &previous),
            "Change memory protection");

        try
        {
            data.CopyTo(new Span<byte>((void*)address, data.Length));
        }
        finally
        {
            var ignored = 0U;

            WindowsNative.Check(WindowsNative.VirtualProtect(address, (nuint)data.Length, previous, &ignored),
                "Restore memory protection");
        }

        if (executable)
        {
            WindowsNative.Check(
                WindowsNative.FlushInstructionCache(WindowsNative.GetCurrentProcess(), address, (nuint)data.Length),
                "Flush instruction cache");
        }
    }
}
