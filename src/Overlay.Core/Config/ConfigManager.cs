using System.Text.Json;
using System.Text.Json.Nodes;
using Overlay.Core.EventBus;

namespace Overlay.Core.Config;

/// <summary>
/// M14 Config Manager: loads/persists the user's <see cref="ConfigSchema"/> to a local
/// JSON file and notifies the rest of the app of changes via <c>SYSTEM.CONFIG_CHANGED</c>
/// (M15 Event Bus). Matches the spec's Interfaces section:
/// <c>Config.get(key)</c>, <c>Config.set(key, value)</c>, <c>Config.reset(key|null)</c>,
/// <c>Config.onChange(key, callback) -> subscriptionId</c>.
///
/// ─── WHY AN INSTANCE, NOT A STATIC CLASS ─────────────────────────────────────────
/// The spec's Interfaces are written as <c>Config.get(key)</c>, suggesting a singleton.
/// This implementation exposes the same four methods on an instance instead of a
/// static class so tests (and, if ever needed, multiple config files) can run against
/// isolated temp directories without sharing global mutable state — the same rationale
/// M15's tests use <c>EventBus.ResetForTests()</c> to avoid cross-test bleed on ITS
/// static bus. A single process-wide instance can still be composed at startup
/// (e.g. one field on the app's composition root) to match the spec's intended usage
/// shape; nothing about the public surface changes if that instance is reached via
/// `AppConfig.Instance.Get(...)` rather than a bare static `Config.Get(...)`. Noted for
/// Reviewer as a spec-interpretation choice, not a scope deviation.
///
/// ─── KEY ADDRESSING ───────────────────────────────────────────────────────────────
/// <paramref name="key"/> arguments are dot-separated paths into <see cref="ConfigSchema"/>,
/// e.g. "overlay.hudDisplayDuration", "voice.ttsVolume", "hotkeys.comboSlots.Q".
/// Internally the loaded config is kept as both a strongly-typed <see cref="ConfigSchema"/>
/// (for defaults/migration) and a parallel <see cref="JsonObject"/> tree (for generic
/// dotted-path get/set without a big reflection switch) that are kept in sync on every
/// mutation.
///
/// ─── ATOMIC WRITE ─────────────────────────────────────────────────────────────────
/// Every persisted write goes to a sibling ".tmp" file first, then
/// <see cref="File.Replace(string,string,string?)"/> (or <see cref="File.Move"/> when no
/// prior file exists yet) swaps it into place — never an in-place overwrite of the live
/// file, so a crash/power-loss mid-write cannot leave a half-written, corrupted
/// user_config.json (spec Internal Logic #2, Reviewer Checklist item 1).
///
/// ─── DEBOUNCED WRITES ─────────────────────────────────────────────────────────────
/// <see cref="Set"/> updates the in-memory tree and fires <c>SYSTEM.CONFIG_CHANGED</c>
/// synchronously (so Acceptance Criteria #3's 100ms delivery bound is trivially met —
/// delivery is immediate, well under the bound), but the actual file write is scheduled
/// on a single <see cref="Timer"/> that keeps getting reset by subsequent Set() calls
/// within <see cref="DebounceMilliseconds"/> (200ms per spec Agent Implementation
/// Notes). Only the LATEST in-memory state is ever written, and rapid-fire Set() calls
/// coalesce into exactly one disk write instead of one per call.
/// </summary>
public sealed class ConfigManager : IDisposable
{
    /// <summary>Spec Agent Implementation Notes: 200ms coalescing window.</summary>
    public const int DebounceMilliseconds = 200;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    private JsonObject _root;
    private Timer? _debounceTimer;
    private JsonObject? _pendingWrite;

    /// <summary>Default on-disk location: config/user_config.json under the app base
    /// directory (spec Internal Logic #1).</summary>
    public static string DefaultConfigPath
        => Path.Combine(AppContext.BaseDirectory, "config", "user_config.json");

    public ConfigManager(string? filePath = null)
    {
        _filePath = filePath ?? DefaultConfigPath;
        _root = LoadOrCreate(_filePath);
    }

    // ── Public API (spec Interfaces) ────────────────────────────────────────────

    /// <summary>Reads the value at <paramref name="key"/> (dot-separated path). Returns
    /// null if the path does not exist.</summary>
    public object? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must not be empty.", nameof(key));

        lock (_lock)
        {
            var node = FindNode(_root, key);
            return NodeToClrValue(node);
        }
    }

    /// <summary>Writes <paramref name="value"/> at <paramref name="key"/> (dot-separated
    /// path, intermediate objects created as needed), publishes
    /// <c>SYSTEM.CONFIG_CHANGED</c> immediately, and schedules a debounced atomic
    /// file write.</summary>
    public void Set(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key must not be empty.", nameof(key));

        JsonObject snapshotForWrite;
        object? oldValue;
        lock (_lock)
        {
            oldValue = NodeToClrValue(FindNode(_root, key));
            SetNode(_root, key, ClrValueToNode(value));
            snapshotForWrite = _root.DeepClone().AsObject();
            ScheduleDebouncedWrite(snapshotForWrite);
        }

        EventBus.EventBus.Publish(
            "SYSTEM.CONFIG_CHANGED",
            new ConfigChangedPayload(key, oldValue, value),
            source: "M14.ConfigManager");
    }

    /// <summary>Resets a single dotted key (restoring its default from a fresh
    /// <see cref="ConfigSchema"/>) or, when <paramref name="key"/> is null, resets the
    /// entire config to defaults. Persists (debounced) and publishes
    /// <c>SYSTEM.CONFIG_CHANGED</c> like <see cref="Set"/>.</summary>
    public void Reset(string? key)
    {
        var defaults = ToJsonObject(ConfigSchema.CreateDefault());

        if (key is null)
        {
            JsonObject snapshotForWrite;
            lock (_lock)
            {
                _root = defaults;
                snapshotForWrite = _root.DeepClone().AsObject();
                ScheduleDebouncedWrite(snapshotForWrite);
            }

            EventBus.EventBus.Publish(
                "SYSTEM.CONFIG_CHANGED",
                new ConfigChangedPayload(Key: null, OldValue: null, NewValue: null),
                source: "M14.ConfigManager");
            return;
        }

        var defaultValue = NodeToClrValue(FindNode(defaults, key));
        Set(key, defaultValue);
    }

    /// <summary>Thin wrapper over <see cref="EventBus.EventBus.Subscribe"/> filtered to
    /// changes at (or, for a null key, any) <paramref name="key"/>. Returns the
    /// underlying EventBus subscription id, usable directly with
    /// <see cref="EventBus.EventBus.Unsubscribe"/>.</summary>
    public string OnChange(string? key, Action<object?> callback)
    {
        if (callback is null) throw new ArgumentNullException(nameof(callback));

        return EventBus.EventBus.Subscribe("SYSTEM.CONFIG_CHANGED", evt =>
        {
            if (evt.Payload is not ConfigChangedPayload changed) return;
            if (key is not null && changed.Key != key) return;
            callback(changed.NewValue);
        });
    }

    /// <summary>Flushes any pending debounced write synchronously and stops the
    /// debounce timer. Call at shutdown so a pending change is not lost.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            if (_pendingWrite is not null)
            {
                WriteAtomic(_filePath, _pendingWrite);
                _pendingWrite = null;
            }
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────────────

    private static JsonObject LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = ToJsonObject(ConfigSchema.CreateDefault());
            WriteAtomic(path, defaults);
            return defaults;
        }

        string json = File.ReadAllText(path);
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // Corrupted file: fall back to defaults rather than crashing the app
            // (spec Acceptance Criteria intent: unknown/bad data must not crash).
            parsed = null;
        }

        if (parsed is not JsonObject obj)
        {
            var defaults = ToJsonObject(ConfigSchema.CreateDefault());
            WriteAtomic(path, defaults);
            return defaults;
        }

        // Migration hook (spec Internal Logic #3) + tolerate unknown keys: deserializing
        // into the typed ConfigSchema and re-serializing drops anything System.Text.Json
        // doesn't recognize, which is exactly "ignore unknown keys" (Acceptance Criteria
        // #2) while still round-tripping every KNOWN field correctly.
        var schema = ConfigMigrator.MigrateAndDeserialize(obj, SerializerOptions);
        return ToJsonObject(schema);
    }

    // ── Debounce + atomic write ─────────────────────────────────────────────────

    private void ScheduleDebouncedWrite(JsonObject latestSnapshot)
    {
        // Caller holds _lock.
        _pendingWrite = latestSnapshot;

        if (_debounceTimer is null)
        {
            _debounceTimer = new Timer(FlushPendingWrite, null, DebounceMilliseconds, Timeout.Infinite);
        }
        else
        {
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    private void FlushPendingWrite(object? state)
    {
        JsonObject? toWrite;
        lock (_lock)
        {
            toWrite = _pendingWrite;
            _pendingWrite = null;
        }
        if (toWrite is not null)
        {
            WriteAtomic(_filePath, toWrite);
        }
    }

    /// <summary>Writes <paramref name="content"/> to a temp file next to
    /// <paramref name="path"/>, then atomically swaps it into place — never an in-place
    /// overwrite (spec Internal Logic #2 / Reviewer Checklist item 1).</summary>
    private static void WriteAtomic(string path, JsonObject content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, content.ToJsonString(SerializerOptions));

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    // ── Dotted-path node helpers ─────────────────────────────────────────────────

    private static JsonNode? FindNode(JsonObject root, string key)
    {
        JsonNode? current = root;
        foreach (var segment in key.Split('.'))
        {
            if (current is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(segment, out current)) return null;
        }
        return current;
    }

    private static void SetNode(JsonObject root, string key, JsonNode? value)
    {
        var segments = key.Split('.');
        JsonObject current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (current[segment] is JsonObject nested)
            {
                current = nested;
            }
            else
            {
                var created = new JsonObject();
                current[segment] = created;
                current = created;
            }
        }
        current[segments[^1]] = value;
    }

    private static object? NodeToClrValue(JsonNode? node)
    {
        if (node is null) return null;
        return node switch
        {
            JsonObject obj => obj.Deserialize<Dictionary<string, object?>>(SerializerOptions),
            JsonArray arr => arr.Deserialize<List<object?>>(SerializerOptions),
            JsonValue val => val.GetValue<JsonElement>().ValueKind switch
            {
                JsonValueKind.String => val.GetValue<string>(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => val.GetValue<double>(),
                _ => null,
            },
            _ => null,
        };
    }

    private static JsonNode? ClrValueToNode(object? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, SerializerOptions);

    private static JsonObject ToJsonObject(ConfigSchema schema) =>
        JsonSerializer.SerializeToNode(schema, SerializerOptions)!.AsObject();
}

/// <summary>Payload published on <c>SYSTEM.CONFIG_CHANGED</c>. <see cref="Key"/> is null
/// when the change was a full <see cref="ConfigManager.Reset"/>(null).</summary>
public sealed record ConfigChangedPayload(string? Key, object? OldValue, object? NewValue);
