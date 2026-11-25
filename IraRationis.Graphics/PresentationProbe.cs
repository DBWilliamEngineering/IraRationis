// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace IraRationis.Graphics;

public static unsafe class PresentationProbe
{
    public static int Execute(string[] args, TextWriter writer)
    {
        var seconds = 10U;
        var fps = 60U;
        var gpuUpload = false;

        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];

            if (option == "--gpu-upload")
            {
                gpuUpload = true;

                continue;
            }

            if (i + 1 >= args.Length
                || !uint.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"Expected an integer for {option}.");
            }

            switch (option)
            {
                case "--seconds" when value is >= 1 and <= 120:
                    seconds = value;

                    break;

                case "--fps" when value is >= 1 and <= 240:
                    fps = value;

                    break;

                default:
                    throw new ArgumentException($"Invalid presentation argument: {option} {value}.");
            }
        }

        var module = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "irx-native.dll"));
        var run = (delegate* unmanaged[Stdcall]<uint, uint, uint, int>)NativeLibrary.GetExport(module,
            "irx_present_probe");

        try
        {
            writer.WriteLine("Synthetic presentation probe. No game executable or video is loaded.");
            var result = run(seconds, fps, gpuUpload ? 1U : 0U);
            writer.WriteLine($"Presentation HRESULT: 0x{unchecked((uint)result):X8}");

            return result < 0 ? 1 : 0;
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }
}
