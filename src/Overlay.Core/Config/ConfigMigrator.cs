using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overlay.Core.Config;

/// <summary>
/// M14 spec Internal Logic #3 hook: "스키마 버전 필드를 두어, 향후 스키마 변경 시 마이그레이션
/// 함수 실행". Each schema bump adds one "if version &lt; N, transform to N" step on the raw
/// JSON, applied in order before the typed deserialize. Steps today:
///  - v2 (2026-10-23, lolF01): turn <c>overlay.items.laneReturn.enabled</c> on once. 0.4.0
///    changed its default to ON, but every older file stored false, so upgraders never got it.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>Upgrades a raw parsed JSON node in place to
    /// <see cref="ConfigSchema.CurrentSchemaVersion"/> if it is from an older schema
    /// version (a missing <c>schemaVersion</c> counts as 0), then returns the deserialized
    /// result. <paramref name="migrated"/> is true when any step ran, so the caller can
    /// persist the upgraded file.</summary>
    public static ConfigSchema MigrateAndDeserialize(JsonNode root, JsonSerializerOptions options, out bool migrated)
    {
        int version = root["schemaVersion"]?.GetValue<int>() ?? 0;
        migrated = version < ConfigSchema.CurrentSchemaVersion;

        if (version < 2)
        {
            // AI-NOTE: one-time ON for upgraders (user decision, lolF01). The caller writes the
            // file as v2 right away, so a later user choice of false is never overridden again.
            var overlay = GetOrCreateObject(root.AsObject(), "overlay");
            var items = GetOrCreateObject(overlay, "items");
            var laneReturn = GetOrCreateObject(items, "laneReturn");
            laneReturn["enabled"] = true;
        }

        var schema = root.Deserialize<ConfigSchema>(options) ?? ConfigSchema.CreateDefault();
        schema.SchemaVersion = ConfigSchema.CurrentSchemaVersion;
        return schema;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}
