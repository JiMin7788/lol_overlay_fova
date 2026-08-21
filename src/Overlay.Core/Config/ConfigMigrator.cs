using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overlay.Core.Config;

/// <summary>
/// M14 spec Internal Logic #3 hook: "스키마 버전 필드를 두어, 향후 스키마 변경 시 마이그레이션
/// 함수 실행". Only <see cref="ConfigSchema.CurrentSchemaVersion"/> (1) exists today, so
/// there is nothing to migrate yet — this is the structural seam a future schema bump
/// would extend (add one "if version == N, transform to N+1" step per bump and keep
/// chaining forward), not a built-out migration chain.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>Upgrades a raw parsed JSON node in place to
    /// <see cref="ConfigSchema.CurrentSchemaVersion"/> if it is from an older schema
    /// version, then returns the deserialized result. No-op today since only version 1
    /// exists.</summary>
    public static ConfigSchema MigrateAndDeserialize(JsonNode root, JsonSerializerOptions options)
    {
        int version = root["schemaVersion"]?.GetValue<int>() ?? 0;

        // Future migrations go here, e.g.:
        // if (version < 2) { /* transform `root` for v1 -> v2 */ version = 2; }

        var schema = root.Deserialize<ConfigSchema>(options) ?? ConfigSchema.CreateDefault();
        schema.SchemaVersion = ConfigSchema.CurrentSchemaVersion;
        return schema;
    }
}
