// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-core/src/config.rs.

using System.Text.Json.Serialization;

namespace IraRationis.Core.Configuration.Models;

public sealed class ConfigDocument
{
    [JsonPropertyName("paths")]
    public ConfigPaths Paths
    {
        get;
        set;
    } = new();
}
