// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-pe/src/image.rs and layout.rs, Zettrox/ztrx.

using System.Text;
using IraRationis.Core;

namespace IraRationis.PE;

public sealed class PeImage
{
    private readonly byte[] _data;
    private readonly DirectoryEntry[] _directories;

    private PeImage(byte[] data)
    {
        _data = (byte[])data.Clone();

        var bytes = _data.AsSpan();

        if (!BinaryData.Slice(bytes, 0, 2).SequenceEqual("MZ"u8))
        {
            throw new InvalidDataException("Missing DOS signature.");
        }

        var ntOffset = (long)BinaryData.ReadUInt32(bytes, 0x3c);

        if (!BinaryData.Slice(bytes, ntOffset, 4).SequenceEqual("PE\0\0"u8))
        {
            throw new InvalidDataException("Missing PE signature.");
        }

        var fileHeaderOffset = ntOffset + 4;
        var optionalHeaderOffset = fileHeaderOffset + 20;

        Machine = BinaryData.ReadUInt16(bytes, fileHeaderOffset);

        var sectionCount = BinaryData.ReadUInt16(bytes, fileHeaderOffset + 2);

        Timestamp = BinaryData.ReadUInt32(bytes, fileHeaderOffset + 4);

        var optionalSize = BinaryData.ReadUInt16(bytes, fileHeaderOffset + 16);

        Characteristics = BinaryData.ReadUInt16(bytes, fileHeaderOffset + 18);

        var optional = BinaryData.Slice(bytes, optionalHeaderOffset, optionalSize);

        if (Machine != 0x8664 || BinaryData.ReadUInt16(optional, 0) != 0x20b)
        {
            throw new InvalidDataException("Expected AMD64 PE32+.");
        }

        EntryRva = BinaryData.ReadUInt32(optional, 16);
        ImageBase = BinaryData.ReadUInt64(optional, 24);
        SizeOfImage = BinaryData.ReadUInt32(optional, 56);
        SizeOfHeaders = BinaryData.ReadUInt32(optional, 60);
        Subsystem = BinaryData.ReadUInt16(optional, 68);
        DllCharacteristics = BinaryData.ReadUInt16(optional, 70);
        StackReserve = BinaryData.ReadUInt64(optional, 72);
        StackCommit = BinaryData.ReadUInt64(optional, 80);

        var count = BinaryData.ReadUInt32(optional, 108);

        if (count > (optional.Length - 112) / 8)
        {
            throw new InvalidDataException("Truncated directory table.");
        }

        _directories = new DirectoryEntry[Math.Min((int)count, 16)];

        for (var i = 0; i < _directories.Length; i++)
        {
            var offset = 112 + i * 8;
            var rva = BinaryData.ReadUInt32(optional, offset);
            var size = BinaryData.ReadUInt32(optional, offset + 4);

            _directories[i] = new DirectoryEntry(rva, size);
        }

        var sections = new List<SectionInfo>(sectionCount);

        for (var i = 0; i < sectionCount; i++)
        {
            var section = BinaryData.Slice(bytes, optionalHeaderOffset + optionalSize + i * 40L, 40);

            sections.Add(new SectionInfo(
                Encoding.UTF8.GetString(section[..8]).TrimEnd('\0'),
                BinaryData.ReadUInt32(section, 12),
                BinaryData.ReadUInt32(section, 8),
                BinaryData.ReadUInt32(section, 20),
                BinaryData.ReadUInt32(section, 16),
                BinaryData.ReadUInt32(section, 36)));
        }

        Sections = sections.AsReadOnly();

        Imports = ParseImports(false);

        DelayImports = ParseImports(true);

        Exports = ParseExports();

        Tls = ParseTls();

        CodeView = ParseCodeView();
    }

    public ushort Machine
    {
        get;
    }

    public ushort Characteristics
    {
        get;
    }

    public uint Timestamp
    {
        get;
    }

    public ushort Subsystem
    {
        get;
    }

    public ushort DllCharacteristics
    {
        get;
    }

    public ulong ImageBase
    {
        get;
    }

    public uint SizeOfImage
    {
        get;
    }

    public uint EntryRva
    {
        get;
    }

    public ulong StackReserve
    {
        get;
    }

    public ulong StackCommit
    {
        get;
    }

    public uint SizeOfHeaders
    {
        get;
    }

    public IReadOnlyList<SectionInfo> Sections
    {
        get;
    }

    public IReadOnlyList<ImportModule> Imports
    {
        get;
    }

    public IReadOnlyList<ImportModule> DelayImports
    {
        get;
    }

    public IReadOnlyList<ExportInfo> Exports
    {
        get;
    }

    public TlsInfo? Tls
    {
        get;
    }

    public CodeViewInfo? CodeView
    {
        get;
    }

    public bool IsDll
    {
        get
        {
            return (Characteristics & 0x2000) != 0;
        }
    }

    public bool HasRelocations
    {
        get
        {
            return Directory(5).Present && (Characteristics & 1) == 0;
        }
    }

    public bool HasExceptionTable
    {
        get
        {
            return Directory(3).Present;
        }
    }

    public bool HasLoadConfig
    {
        get
        {
            return Directory(10).Present;
        }
    }

    public int ImportCount
    {
        get
        {
            return Imports.Sum(module => module.Entries.Count);
        }
    }

    public string SubsystemName
    {
        get
        {
            return Subsystem switch
            {
                2 => "windows-gui",
                3 => "windows-cui",
                14 => "xbox",
                17 => "xbox-code-catalog",
                _ => "other"
            };
        }
    }

    public static PeImage Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new PeImage(data);
    }

    public DirectoryEntry Directory(int index)
    {
        if (index < 0 || index >= _directories.Length)
        {
            return default;
        }

        return _directories[index];
    }

    public uint? RvaToFileOffset(uint rva)
    {
        foreach (var section in Sections)
        {
            if (rva >= section.Rva && rva - section.Rva < section.BackedSize)
            {
                var offset = (ulong)section.FileOffset + rva - section.Rva;

                return offset < (ulong)_data.Length ? (uint)offset : null;
            }
        }

        return null;
    }

    private ReadOnlySpan<byte> AtRva(uint rva)
    {
        if (rva < SizeOfHeaders)
        {
            return BinaryData.Slice(_data, rva, Math.Min(SizeOfHeaders, _data.Length) - rva);
        }

        foreach (var section in Sections)
        {
            if (rva >= section.Rva && rva - section.Rva < section.BackedSize)
            {
                var delta = rva - section.Rva;

                return BinaryData.Slice(_data, (long)section.FileOffset + delta, section.BackedSize - delta);
            }
        }

        throw new InvalidDataException($"RVA 0x{rva:X} is not file-backed.");
    }

    private string NameAt(uint rva)
    {
        return BinaryData.ReadNullTerminatedString(AtRva(rva));
    }

    private IReadOnlyList<ImportModule> ParseImports(bool delay)
    {
        var directory = Directory(delay ? 13 : 1);

        if (!directory.Present)
        {
            return Array.Empty<ImportModule>();
        }

        var modules = new List<ImportModule>();
        var stride = delay ? 32 : 20;
        var table = BinaryData.Slice(AtRva(directory.Rva), 0, directory.Size);

        for (var offset = 0; offset <= table.Length - stride; offset += stride)
        {
            var descriptor = table.Slice(offset, stride);

            if (descriptor.IndexOfAnyExcept((byte)0) < 0)
            {
                return modules.AsReadOnly();
            }

            if (delay && BinaryData.ReadUInt32(descriptor, 0) != 1)
            {
                throw new InvalidDataException("Unsupported delay import VA descriptor.");
            }

            var name = BinaryData.ReadUInt32(descriptor, delay ? 4 : 12);
            var iat = BinaryData.ReadUInt32(descriptor, delay ? 12 : 16);
            var names = BinaryData.ReadUInt32(descriptor, delay ? 16 : 0);

            if (iat == 0 || name == 0)
            {
                throw new InvalidDataException("Missing import name or IAT.");
            }

            var moduleName = NameAt(name);
            var entries = ParseThunks(names == 0 ? iat : names, iat);

            modules.Add(new ImportModule(moduleName, entries));
        }

        throw new InvalidDataException("Unterminated import directory.");
    }

    private IReadOnlyList<ImportEntry> ParseThunks(uint names, uint iat)
    {
        var table = AtRva(names);
        var entries = new List<ImportEntry>();

        for (var i = 0; i <= table.Length - 8; i += 8)
        {
            var thunk = BinaryData.ReadUInt64(table, i);

            if (thunk == 0)
            {
                return entries.AsReadOnly();
            }

            var slot = (ulong)iat + (uint)i;

            if (slot + 8 > SizeOfImage)
            {
                throw new InvalidDataException("IAT slot outside image.");
            }

            var symbol = ParseImportSymbol(thunk);

            entries.Add(new ImportEntry(symbol, (uint)slot));
        }

        throw new InvalidDataException("Unterminated import thunk list.");
    }

    private ImportSymbol ParseImportSymbol(ulong thunk)
    {
        if ((thunk & (1UL << 63)) != 0)
        {
            return new ImportSymbol(null, (ushort)thunk);
        }

        if (thunk > uint.MaxValue)
        {
            throw new InvalidDataException("Import name RVA overflow.");
        }

        var name = AtRva((uint)thunk);
        var hint = BinaryData.ReadUInt16(name, 0);
        var symbolName = BinaryData.ReadNullTerminatedString(name[2..]);

        return new ImportSymbol(symbolName, hint);
    }

    private IReadOnlyList<ExportInfo> ParseExports()
    {
        var directory = Directory(0);

        if (!directory.Present)
        {
            return Array.Empty<ExportInfo>();
        }

        var header = BinaryData.Slice(AtRva(directory.Rva), 0, directory.Size);

        if (header.Length < 40)
        {
            throw new InvalidDataException("Truncated export directory.");
        }

        var ordinalBase = BinaryData.ReadUInt32(header, 16);
        var functionCount = BinaryData.ReadUInt32(header, 20);
        var nameCount = BinaryData.ReadUInt32(header, 24);

        if (functionCount == 0)
        {
            return Array.Empty<ExportInfo>();
        }

        var result = new List<ExportInfo>();
        var functions = BinaryData.Slice(AtRva(BinaryData.ReadUInt32(header, 28)), 0, functionCount * 4L);
        var names = new Dictionary<uint, string>();

        if (nameCount > 0)
        {
            var nameTable = BinaryData.Slice(AtRva(BinaryData.ReadUInt32(header, 32)), 0, nameCount * 4L);
            var ordinals = BinaryData.Slice(AtRva(BinaryData.ReadUInt32(header, 36)), 0, nameCount * 2L);

            for (var i = 0U; i < nameCount; i++)
            {
                var index = BinaryData.ReadUInt16(ordinals, i * 2L);

                if (index >= functionCount)
                {
                    throw new InvalidDataException("Invalid export ordinal index.");
                }

                names.TryAdd(index, NameAt(BinaryData.ReadUInt32(nameTable, i * 4L)));
            }
        }

        for (var i = 0U; i < functionCount; i++)
        {
            var rva = BinaryData.ReadUInt32(functions, i * 4L);

            if (rva == 0)
            {
                continue;
            }

            var forwarded = rva >= directory.Rva && rva < (ulong)directory.Rva + directory.Size;
            var forward = forwarded
                ? BinaryData.ReadNullTerminatedString(BinaryData.Slice(
                    AtRva(rva), 0, (long)((ulong)directory.Rva + directory.Size - rva)))
                : null;

            result.Add(new ExportInfo(
                names.GetValueOrDefault(i),
                checked(ordinalBase + i),
                forwarded ? null : rva,
                forward));
        }

        return result.AsReadOnly();
    }

    private TlsInfo? ParseTls()
    {
        var directory = Directory(9);

        if (directory.Rva == 0)
        {
            return null;
        }

        if (directory.Size < 40)
        {
            throw new InvalidDataException("Truncated TLS directory.");
        }

        var tlsDirectory = BinaryData.Slice(AtRva(directory.Rva), 0, 40);
        var va = BinaryData.ReadUInt64(tlsDirectory, 24);
        var callbacks = new List<uint>();

        if (va != 0)
        {
            var array = AtRva(VaToRva(va));

            for (var i = 0; i < 1024; i++)
            {
                var callback = BinaryData.ReadUInt64(array, i * 8L);

                if (callback == 0)
                {
                    return new TlsInfo(directory.Rva, va, callbacks.AsReadOnly());
                }

                callbacks.Add(VaToRva(callback));
            }

            throw new InvalidDataException("Too many TLS callbacks.");
        }

        return new TlsInfo(directory.Rva, va, callbacks.AsReadOnly());
    }

    private uint VaToRva(ulong va)
    {
        if (va < ImageBase || va - ImageBase >= SizeOfImage)
        {
            throw new InvalidDataException("VA outside image address range.");
        }

        return (uint)(va - ImageBase);
    }

    private CodeViewInfo? ParseCodeView()
    {
        var directory = Directory(6);

        if (!directory.Present)
        {
            return null;
        }

        var table = BinaryData.Slice(AtRva(directory.Rva), 0, directory.Size);

        if (table.Length % 28 != 0)
        {
            throw new InvalidDataException("Truncated debug directory.");
        }

        for (var i = 0; i <= table.Length - 28; i += 28)
        {
            if (BinaryData.ReadUInt32(table, i + 12) != 2)
            {
                continue;
            }

            var data = BinaryData.Slice(_data, BinaryData.ReadUInt32(table, i + 24),
                BinaryData.ReadUInt32(table, i + 16));

            if (data.Length >= 24 && data[..4].SequenceEqual("RSDS"u8))
            {
                return new CodeViewInfo(
                    BinaryData.ReadNullTerminatedString(data[24..]),
                    new Guid(data.Slice(4, 16)),
                    BinaryData.ReadUInt32(data, 20));
            }
        }

        return null;
    }
}
