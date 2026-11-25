// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IraRationis.Loader;

public sealed unsafe class ConsoleInstructionHandler : IDisposable
{
    private readonly delegate* unmanaged[Stdcall]<void> _disable;
    private bool _installed;

    public ConsoleInstructionHandler(IEnumerable<LoadedImage> images)
        : this(images.Select(image =>
            new ImageRange(image.BaseAddress, checked(image.BaseAddress + image.Info.SizeOfImage))).ToArray())
    {
    }

    private ConsoleInstructionHandler(ImageRange[] ranges)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "irx-native.dll");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Native VEH component not found. Build with the Visual Studio C++ workload installed.", path);
        }

        // Intentionally keep the native DLL loaded for the process lifetime.
        var module = NativeLibrary.Load(path);
        var install =
            (delegate* unmanaged[Stdcall]<ImageRange*, uint, uint>)NativeLibrary.GetExport(module, "IrxInstallHandler");
        _disable = (delegate* unmanaged[Stdcall]<void>)NativeLibrary.GetExport(module, "IrxDisableHandler");

        fixed (ImageRange* pointer = ranges)
        {
            var error = install(pointer, (uint)ranges.Length);

            if (error != 0)
            {
                throw new Win32Exception((int)error, "Cannot register native CPU compatibility handler.");
            }
        }

        _installed = true;
    }

    public void Dispose()
    {
        if (_installed)
        {
            _disable();
            _installed = false;
        }
    }

    public static void VerifyManagedExceptionDispatch()
    {
        // A non-executable dummy range: no instruction can be emulated by this probe.
        using var handler = new ConsoleInstructionHandler([new ImageRange(1, 2)]);

        try
        {
            ThrowProbeException();
        }
        catch (InvalidOperationException exception) when (exception.Message == "VEH probe")
        {
            return;
        }

        throw new InvalidOperationException("VEH probe did not catch the expected managed exception.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowProbeException()
    {
        throw new InvalidOperationException("VEH probe");
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ImageRange
    {
        public readonly nuint Start;
        public readonly nuint End;

        public ImageRange(nuint start, nuint end)
        {
            Start = start;
            End = end;
        }
    }
}
