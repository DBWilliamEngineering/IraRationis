// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.Shaders;

namespace IraRationis.Cli;

internal static class ShaderCommand
{
    public static void Execute(byte[] data, TextWriter writer)
    {
        var container = ShaderContainer.Parse(data);

        writer.WriteLine($"DXBC {container.Major}.{container.Minor}");

        foreach (var part in container.Parts)
        {
            writer.WriteLine($"{part.FourCC} offset=0x{part.Offset:X} size={part.Data.Length}");

            if (part.FourCC != "DXIL")
            {
                continue;
            }

            var program = DxilProgram.Parse(part.Data);

            writer.WriteLine($"kind={(ushort)program.Kind} SM={program.Major}.{program.Minor} XDAT={program.HasXdat} xbop={program.HasXbop}");
        }
    }
}
