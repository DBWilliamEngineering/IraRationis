// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IraRationis.Cli.Diagnostics.Instructions;

internal sealed class InstructionProbeMapping : IDisposable
{
    private readonly SafeFileHandle _section;
    private readonly List<nint> _views = [];
    private readonly nuint _size;

    internal InstructionProbeMapping(nuint size)
    {
        var section = InstructionProbeNativeMethods.CreateFileMappingW(-1, 0,
            InstructionProbeNativeMethods.ReadWrite, (uint)((ulong)size >> 32), (uint)size, null);

        if (section == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot create instruction probe section.");
        }

        _section = new SafeFileHandle(section, true);
        _size = size;
    }

    internal nuint Map(uint access)
    {
        var view = InstructionProbeNativeMethods.MapViewOfFile(_section.DangerousGetHandle(), access, 0, 0, _size);

        GC.KeepAlive(_section);

        if (view == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot map instruction probe section.");
        }

        try
        {
            _views.Add(view);
        }
        catch
        {
            _ = InstructionProbeNativeMethods.UnmapViewOfFile(view);

            throw;
        }

        return (nuint)view;
    }

    public void Dispose()
    {
        var firstError = 0;

        foreach (var view in _views)
        {
            if (InstructionProbeNativeMethods.UnmapViewOfFile(view) == 0 && firstError == 0)
            {
                firstError = Marshal.GetLastPInvokeError();
            }
        }

        _views.Clear();
        _section.Dispose();

        if (firstError != 0)
        {
            throw new Win32Exception(firstError, "Cannot unmap instruction probe section.");
        }
    }
}
