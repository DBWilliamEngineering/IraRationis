// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from victory-loader/src/windows.rs: dependency ordering and export resolution.
using IraRationis.PE;

namespace IraRationis.Loader;

public sealed class TitleImages(string titleDirectory, string cacheDirectory, Func<string, bool> wanted, IImportResolver inner) : IImportResolver
{
    private readonly Dictionary<string, LoadedImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LoadedImage> _ordered = [];

    public IReadOnlyList<LoadedImage> Ordered
    {
        get
        {
            return _ordered;
        }
    }

    public void MapDependencies(PeImage info)
    {
        foreach (var module in info.Imports.Concat(info.DelayImports))
        {
            var name = module.Dll;

            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.Contains('\0') || !wanted(name) || _images.ContainsKey(name))
            {
                continue;
            }

            var path = Path.Combine(titleDirectory, name);

            if (!File.Exists(path))
            {
                continue;
            }

            var prepared = ImagePreparation.Prepare(File.ReadAllBytes(path));

            if (!prepared.Info.IsDll)
            {
                throw new InvalidDataException($"Expected a DLL: {path}");
            }

            var image = LoadedImage.Load(prepared, cacheDirectory);
            _images.Add(name, image);
            MapDependencies(image.Info);
            _ordered.Add(image);
        }
    }

    public ImportResolution? Resolve(string dll, ImportSymbol symbol)
    {
        var result = inner.Resolve(dll, symbol);

        if (result.HasValue)
        {
            return result;
        }

        if (_images.TryGetValue(dll, out var image) && image.Export(symbol) is { } address)
        {
            return new ImportResolution(address, ImportProvenance.Title);
        }

        return null;
    }

    public string GetOriginalPath(LoadedImage image)
    {
        foreach (var (name, value) in _images)
        {
            if (ReferenceEquals(value, image))
            {
                return Path.Combine(titleDirectory, name);
            }
        }

        throw new ArgumentException("Unknown title image.", nameof(image));
    }
}
