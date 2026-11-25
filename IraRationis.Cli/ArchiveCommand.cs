// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Game;

namespace IraRationis.Cli;

internal static class ArchiveCommand
{
    public static void Execute(ReadOnlyMemory<byte> data, TextWriter output)
    {
        var count = 0;

        foreach (var record in Sgd5.Records(data))
        {
            var bytes = record.Unpack();
            var description = bytes is null ? "invalid deflate stream" : $"{bytes.Length} bytes";
            output.WriteLine($"0x{record.Offset:X8} {record.Kind:X2} {record.Name ?? "<unnamed>"}: {description}");
            count++;
        }

        output.WriteLine($"Shader records: {count}");
    }
}
