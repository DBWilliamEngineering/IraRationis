// SPDX-License-Identifier: GPL-3.0-or-later
// Port of Zettrox/ztrx, crates/victory-hle/src/runtime.rs (runtime state and title metadata).

using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace IraRationis.HLE;

public sealed record Title(uint Id, string StoreId, string Storage, ulong StorageBytes)
{
    public static Title Empty { get; } = new(0, string.Empty, string.Empty, 0);

    public static Title FromManifest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        // The original uses a small tolerant XML scanner. Use the standard XML reader for
        // valid manifests, prohibit DTDs and retain zero/empty defaults for invalid input.
        try
        {
            using var input = new StringReader(text.TrimStart('\uFEFF'));
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var root = XDocument.Load(reader).Root;

            if (root is null || root.Name != "Game")
            {
                return Empty;
            }

            var idText = root.Element("TitleId")?.Value.Trim();
            var sizeText = root.Element("PersistentLocalStorage")?.Element("SizeMB")?.Value.Trim();
            _ = uint.TryParse(idText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id);
            _ = ulong.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var sizeMb);

            return new Title(id, root.Element("StoreId")?.Value.Trim() ?? string.Empty, string.Empty, sizeMb << 20);
        }
        catch (XmlException)
        {
            return Empty;
        }
    }

    public string GetSavesPath()
    {
        return Path.Combine(Path.GetDirectoryName(Storage) ?? string.Empty, "saves");
    }
}

public static class RuntimeState
{
    private static readonly Lock StateLock = new();
    private static readonly Dictionary<nuint, string> ImagePaths = new();
    private static nint _mainImage;
    private static Func<string, nuint?>? _titleDllLoader;
    private static Title _title = Title.Empty;

    public static nuint? MainImage
    {
        get
        {
            var address = Volatile.Read(ref _mainImage);

            return address == 0 ? null : (nuint)address;
        }
    }

    public static Title CurrentTitle
    {
        get
        {
            lock (StateLock)
            {
                return _title;
            }
        }
        set
        {
            lock (StateLock)
            {
                _title = value;
            }
        }
    }

    public static void SetMainImage(nuint address)
    {
        Volatile.Write(ref _mainImage, (nint)address);
    }

    public static void SetImagePath(nuint address, string path)
    {
        var absolute = Path.GetFullPath(path);

        lock (StateLock)
        {
            ImagePaths.Remove(address);
            ImagePaths.Add(address, absolute);
        }
    }

    public static void ForgetImage(nuint address)
    {
        lock (StateLock)
        {
            ImagePaths.Remove(address);
        }
    }

    public static string? GetImagePath(nuint address)
    {
        lock (StateLock)
        {
            return ImagePaths.GetValueOrDefault(address);
        }
    }

    public static nuint? FindImage(string file)
    {
        lock (StateLock)
        {
            foreach (var (address, path) in ImagePaths)
            {
                if (string.Equals(Path.GetFileName(path), file, StringComparison.OrdinalIgnoreCase))
                {
                    return address;
                }
            }
        }

        return null;
    }

    public static void SetTitleDllLoader(Func<string, nuint?> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        Interlocked.CompareExchange(ref _titleDllLoader, loader, null);
    }

    public static nuint? LoadTitleDll(string file)
    {
        var loader = Volatile.Read(ref _titleDllLoader);

        return loader?.Invoke(file);
    }
}
