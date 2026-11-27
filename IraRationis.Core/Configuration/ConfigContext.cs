// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from Zettrox/ztrx crates/victory-core/src/config.rs.

using Tomlyn;
using Tomlyn.Serialization;
using IraRationis.Core.Configuration.Models;

namespace IraRationis.Core.Configuration;

[TomlSerializable(typeof(ConfigDocument))]
internal partial class ConfigContext : TomlSerializerContext
{
    public ConfigContext(TomlTypeInfo<ConfigDocument>? configDocument, TomlTypeInfo<ConfigPaths>? configPaths,
        TomlTypeInfo<string>? stringTypeInfo, TomlTypeInfo<string[]>? stringArray)
    {
        _ConfigDocument = configDocument;
        _ConfigPaths = configPaths;
        _String = stringTypeInfo;
        _StringArray = stringArray;
    }

    public ConfigContext(TomlSerializerOptions options, TomlTypeInfo<ConfigDocument>? configDocument,
        TomlTypeInfo<ConfigPaths>? configPaths, TomlTypeInfo<string>? stringTypeInfo,
        TomlTypeInfo<string[]>? stringArray) : base(options)
    {
        _ConfigDocument = configDocument;
        _ConfigPaths = configPaths;
        _String = stringTypeInfo;
        _StringArray = stringArray;
    }
}
