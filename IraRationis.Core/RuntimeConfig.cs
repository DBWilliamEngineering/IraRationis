// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-core/src/config.rs.

using System.Text.Json.Serialization;
using Tomlyn;
using Tomlyn.Serialization;

namespace IraRationis.Core;

public sealed record RuntimeConfig(
    string? GameDirectory,
    string CacheDirectory,
    string DataDirectory,
    IReadOnlyList<string> TestTitles,
    string? Source)
{
    public static RuntimeConfig Load()
    {
        var explicitPath = GetEnvironment("CONFIG");
        var path = explicitPath ?? "victory.toml";
        var source = File.Exists(path) ? path : null;

        if (source is null && explicitPath is not null)
        {
            throw new FileNotFoundException("Configured TOML file does not exist.", path);
        }

        var document = source is null
            ? new ConfigDocument()
            : TomlSerializer.Deserialize(File.ReadAllText(source), ConfigContext.Default.ConfigDocument)
              ?? throw new InvalidDataException("Empty configuration document.");

        return Resolve(document.Paths, source, GetEnvironment);
    }

    public static RuntimeConfig Resolve(ConfigPaths paths, string? source, Func<string, string?> environment)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "IraRationis");
        var testTitles = NonEmpty(environment("TEST_TITLES"));
        var titles = testTitles?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? paths.TestTitles;

        return new RuntimeConfig(
            NonEmpty(environment("GAME_DIR")) ?? paths.GameDirectory,
            NonEmpty(environment("CACHE_DIR")) ?? paths.CacheDirectory ?? Path.Combine(root, "cache"),
            NonEmpty(environment("DATA_DIR")) ?? paths.DataDirectory ?? Path.Combine(root, "data"),
            titles,
            source);
    }

    public string RequireGameDirectory()
    {
        return GameDirectory ?? throw new InvalidOperationException("Configure IRX_GAME_DIR or [paths].game_dir.");
    }

    public string CacheSubdirectory(string name)
    {
        return CreateSubdirectory(CacheDirectory, name);
    }

    public string DataSubdirectory(string name)
    {
        return CreateSubdirectory(DataDirectory, name);
    }

    private static string CreateSubdirectory(string root, string name)
    {
        var path = AssetPolicy.EnsureOutsideRepository(Path.Combine(root, name));
        Directory.CreateDirectory(path);

        return path;
    }

    private static string? GetEnvironment(string key)
    {
        return NonEmpty(Environment.GetEnvironmentVariable($"IRX_{key}"))
               ?? NonEmpty(Environment.GetEnvironmentVariable($"VICTORY_{key}"));
    }

    private static string? NonEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

public sealed class ConfigDocument
{
    [JsonPropertyName("paths")]
    public ConfigPaths Paths
    {
        get;
        set;
    } = new();
}

public sealed class ConfigPaths
{
    [JsonPropertyName("game_dir")]
    public string? GameDirectory
    {
        get;
        set;
    }

    [JsonPropertyName("cache_dir")]
    public string? CacheDirectory
    {
        get;
        set;
    }

    [JsonPropertyName("data_dir")]
    public string? DataDirectory
    {
        get;
        set;
    }

    [JsonPropertyName("test_titles")]
    public string[] TestTitles
    {
        get;
        set;
    } = [];
}

[TomlSerializable(typeof(ConfigDocument))]
internal partial class ConfigContext : TomlSerializerContext
{
}
