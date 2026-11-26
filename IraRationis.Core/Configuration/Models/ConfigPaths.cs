// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-core/src/config.rs.

using System.Text.Json.Serialization;

namespace IraRationis.Core.Configuration.Models;

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
