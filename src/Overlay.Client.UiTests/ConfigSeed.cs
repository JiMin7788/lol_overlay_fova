using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Overlay.Core.Config;

namespace Overlay.Client.UiTests;

/// <summary>
/// The seed <c>config/user_config.json</c> a session starts with: <see cref="ConfigSchema"/>'s
/// defaults, the harness defaults of spec R5, then the test's overrides.
/// </summary>
public static class ConfigSeed
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Spec R5 defaults besides <c>telemetry.endpoint</c>, which is the session's listener.</summary>
    public static IReadOnlyDictionary<string, object?> HarnessDefaults { get; } = new Dictionary<string, object?>
    {
        ["ads.enabled"] = false,
        ["champSelect.enabled"] = false,
        // AI-NOTE: ConfigSchema defaults to en-US; the tests read the Korean UI text.
        ["general.language"] = "ko",
        ["general.firstRunGuideShown"] = true,
        ["telemetry.noticeShown"] = true,
    };

    /// <summary>The full seed as JSON. Throws for an override key that is not in
    /// <see cref="ConfigSchema"/>, since the app's loader would silently drop it.</summary>
    public static JsonObject Build(string telemetryEndpoint, IReadOnlyDictionary<string, object?>? overrides)
    {
        var root = JsonSerializer.SerializeToNode(ConfigSchema.CreateDefault(), Options)!.AsObject();
        foreach (var (key, value) in HarnessDefaults) Set(root, key, value);
        Set(root, "telemetry.endpoint", telemetryEndpoint);
        if (overrides is not null)
            foreach (var (key, value) in overrides)
            {
                if (!IsSchemaKey(key))
                    throw new ArgumentException(
                        $"Seed key '{key}' is not in ConfigSchema; the app would drop it on load.", nameof(overrides));
                Set(root, key, value);
            }
        return root;
    }

    public static void Write(string path, string telemetryEndpoint, IReadOnlyDictionary<string, object?>? overrides)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Build(telemetryEndpoint, overrides).ToJsonString(Options));
    }

    /// <summary>True when the dotted <paramref name="key"/> names a <see cref="ConfigSchema"/> property
    /// (by its JSON name), or anything under a dictionary property such as <c>combos.saved.&lt;id&gt;</c>.</summary>
    public static bool IsSchemaKey(string key)
    {
        Type type = typeof(ConfigSchema);
        foreach (var segment in key.Split('.'))
        {
            if (IsDictionary(type)) return true;
            var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => JsonName(p) == segment);
            if (property is null) return false;
            type = property.PropertyType;
        }
        return true;
    }

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type)
        || type.GetInterfaces().Append(type).Any(i => i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private static string JsonName(PropertyInfo p) =>
        p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;

    /// <summary>Dotted-path set, creating intermediate objects.</summary>
    public static void Set(JsonObject root, string key, object? value)
    {
        var segments = key.Split('.');
        var current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[segments[i]] = next;
            }
            current = next;
        }
        current[segments[^1]] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, Options);
    }

    /// <summary>Dotted-path read; null when missing.</summary>
    public static JsonNode? Get(JsonNode? root, string key)
    {
        var current = root;
        foreach (var segment in key.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current)) return null;
        }
        return current;
    }
}
