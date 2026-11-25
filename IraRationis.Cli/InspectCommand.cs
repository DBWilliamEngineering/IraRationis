// SPDX-License-Identifier: GPL-3.0-or-later

using IraRationis.PE;

namespace IraRationis.Cli;

internal static class InspectCommand
{
    public static void Execute(byte[] data, TextWriter writer)
    {
        var image = PeImage.Parse(data);

        writer.WriteLine($"AMD64 {image.SubsystemName} base=0x{image.ImageBase:X} entry=0x{image.EntryRva:X} size=0x{image.SizeOfImage:X}");

        foreach (var section in image.Sections)
        {
            writer.WriteLine($"section {section.Name} RVA=0x{section.Rva:X} raw=0x{section.FileOffset:X}");
        }

        PrintImports(image.Imports, "import", writer);
        PrintImports(image.DelayImports, "delay", writer);

        foreach (var export in image.Exports)
        {
            var target = export.Forward ?? $"RVA=0x{export.Rva:X}";

            writer.WriteLine($"export #{export.Ordinal} {export.Name} {target}");
        }

        writer.WriteLine($"relocations={image.HasRelocations} exceptions={image.HasExceptionTable} loadconfig={image.HasLoadConfig}");

        if (image.Tls is { } tls)
        {
            var callbacks = string.Join(", ", tls.CallbackRvas.Select(rva => $"0x{rva:X}"));

            writer.WriteLine($"TLS callbacks: {callbacks}");
        }

        if (image.CodeView is { } codeView)
        {
            writer.WriteLine($"PDB {codeView.PdbPath} {codeView.SymbolKey}");
        }
    }

    private static void PrintImports(IReadOnlyList<ImportModule> modules, string label, TextWriter writer)
    {
        foreach (var module in modules)
        {
            foreach (var entry in module.Entries)
            {
                writer.WriteLine($"{label} {module.Dll}!{entry.Symbol} IAT=0x{entry.IatSlotRva:X}");
            }
        }
    }
}
