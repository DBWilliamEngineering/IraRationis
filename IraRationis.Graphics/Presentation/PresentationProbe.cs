// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace IraRationis.Graphics.Presentation;

public static class PresentationProbe
{
    private const uint DefaultDurationSeconds = 10;
    private const uint MaximumDurationSeconds = 120;
    private const uint DefaultFramesPerSecond = 60;
    private const uint MaximumFramesPerSecond = 240;

    public static int Execute(string[] args, TextWriter writer)
    {
        var seconds = DefaultDurationSeconds;
        var framesPerSecond = DefaultFramesPerSecond;
        var gpuUpload = false;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];

            if (option == "--gpu-upload")
            {
                gpuUpload = true;

                continue;
            }

            if (index + 1 >= args.Length
                || !uint.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new ArgumentException($"Expected an integer for {option}.");
            }

            switch (option)
            {
                case "--seconds" when value is >= 1 and <= MaximumDurationSeconds:
                    seconds = value;

                    break;

                case "--fps" when value is >= 1 and <= MaximumFramesPerSecond:
                    framesPerSecond = value;

                    break;

                default:
                    throw new ArgumentException($"Invalid presentation argument: {option} {value}.");
            }
        }

        writer.WriteLine("Synthetic presentation probe. No game executable or video is loaded.");

        using var session = new PresentationProbeSession(writer, gpuUpload);

        var result = session.Run(seconds, framesPerSecond);

        writer.WriteLine($"Presentation HRESULT: 0x{unchecked((uint)result):X8}");

        return result < 0 ? 1 : 0;
    }
}
