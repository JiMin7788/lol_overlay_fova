using System.Text.Json;
using Overlay.Core.ChampionDb;
using Overlay.Core.Config;

namespace Overlay.Core.Combo;

/// <summary>
/// M04 Combo Editor (docs/modules/M04_COMBO_EDITOR.md): the backing logic/state layer a
/// WPF combo-builder view binds to. Provides create/add/update/remove/reorder editing of
/// a <see cref="ComboGraph"/>, hotkey↔combo mapping, JSON import/export, persistence, and
/// a real-time damage/killangle preview.
///
/// ─── SCOPE ──────────────────────────────────────────────────────────────────────────
/// The actual WPF views/XAML are OUT OF SCOPE here (they live in Overlay.Client, M02's
/// territory) — this module delivers only the pure, unit-testable editing logic, exactly
/// like M03/M05/M06 delivered logic layers with xUnit coverage.
///
/// ─── REUSE, NEVER RE-IMPLEMENT ──────────────────────────────────────────────────────
/// - Export delegates to <see cref="ComboEngine.Serialize"/>; import delegates to
///   <see cref="ComboEngine.Deserialize"/> after schema validation (see <see cref="ImportCombo"/>).
/// - Preview delegates to <see cref="ComboEngine.BuildGraph"/> + <see cref="ComboEngine.Execute"/>
///   — no damage/killangle math is duplicated in this file (Reviewer Checklist item 3).
///
/// ─── NO INPUT AUTOMATION ────────────────────────────────────────────────────────────
/// <see cref="BindHotkey"/> only records a "hotkey string ↔ comboId" mapping (persisted via
/// M14). It sends NO synthetic keypress and calls NO OS input API — actually triggering a
/// bound combo later is M13's job (out of scope). This is the P4 (assistive-only) boundary
/// and Reviewer Checklist item 1.
/// </summary>
public sealed class ComboEditor
{
    /// <summary>Config key prefix under which each saved combo is persisted (as a serialized
    /// <see cref="SavedCombo"/> JSON string). A dedicated key rather than a schema-class
    /// change, per the M04 task note ("keep changes surgical").</summary>
    private const string SavedComboKeyPrefix = "combos.saved.";

    /// <summary>Config path for the hotkey↔combo mapping. Reuses the existing
    /// <c>hotkeys.comboSlots</c> Dictionary&lt;string,string&gt; in the M14 schema
    /// (hotkey string -> combo id); no schema class is extended.</summary>
    private const string HotkeyMapKeyPrefix = "hotkeys.comboSlots.";

    /// <summary>Composite-key separator joining a raw hotkey string to the champion it is bound
    /// for (e.g. <c>"A::Ahri"</c>), so two different champions' combos can share the same raw
    /// hotkey without overwriting each other — the flat <c>hotkey -&gt; comboId</c> map had no
    /// champion dimension before this, so binding Garen's combo to "A" silently unbound Ahri's
    /// combo from "A" (loop 44 bug 1). <see cref="ConfigManager"/> splits config keys on '.' only,
    /// so "::" inside one path segment is safe and adds no extra nesting level.</summary>
    private const string SlotKeySeparator = "::";

    private static readonly JsonSerializerOptions SavedComboOptions = new();

    private readonly ComboEngine _engine;
    private readonly ConfigManager _config;
    private readonly Dictionary<string, EditSession> _sessions = new(StringComparer.Ordinal);

    public ComboEditor(ComboEngine engine, ConfigManager config)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    // ── Editing (spec Interfaces) ────────────────────────────────────────────────

    /// <summary>Creates a fresh, empty editing session for <paramref name="championId"/> and
    /// returns a <see cref="ComboDraft"/> snapshot. The draft carries both the generated
    /// <see cref="ComboDraft.Id"/> (needed to address subsequent edits) and the
    /// <see cref="ComboDraft.Graph"/> (the spec's literal <c>-&gt; ComboGraph</c> return
    /// value) — a ComboGraph alone has no id, so a UI could not otherwise reference the
    /// combo it just created. Documented spec-interpretation choice (see M04 report).</summary>
    public ComboDraft CreateCombo(string championId, string name)
    {
        if (string.IsNullOrWhiteSpace(championId))
            throw new ArgumentException("championId must not be empty", nameof(championId));

        var id = Guid.NewGuid().ToString("N");
        var session = new EditSession(id, championId, name ?? string.Empty);
        _sessions[id] = session;
        return Snapshot(session);
    }

    /// <summary>Appends a node to the combo sequence. Rejects a duplicate node id (the same
    /// uniqueness rule <see cref="ComboEngine.BuildGraph"/> enforces, surfaced early with a
    /// clear message).</summary>
    public void AddNode(string comboId, ComboNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var session = GetSession(comboId);
        if (string.IsNullOrWhiteSpace(node.Id))
            throw new ArgumentException("ComboNode.Id is required", nameof(node));
        if (session.Nodes.Any(n => n.Id == node.Id))
            throw new ArgumentException($"Duplicate ComboNode.Id '{node.Id}'", nameof(node));
        session.Nodes.Add(node);
    }

    /// <summary>Replaces the node whose id is <paramref name="nodeId"/> with
    /// <paramref name="patch"/> applied to it. <paramref name="patch"/> is a pure
    /// old-node -&gt; new-node function (idiomatic for the immutable <see cref="ComboNode"/>
    /// record, e.g. <c>n =&gt; n with { Damage = 120 }</c>).</summary>
    public void UpdateNode(string comboId, string nodeId, Func<ComboNode, ComboNode> patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var session = GetSession(comboId);
        int index = IndexOfNode(session, nodeId);
        session.Nodes[index] = patch(session.Nodes[index])
            ?? throw new ArgumentException("patch must not return null", nameof(patch));
    }

    /// <summary>Removes the node whose id is <paramref name="nodeId"/>.</summary>
    public void RemoveNode(string comboId, string nodeId)
    {
        var session = GetSession(comboId);
        session.Nodes.RemoveAt(IndexOfNode(session, nodeId));
    }

    /// <summary>Reorders the sequence to match <paramref name="orderedNodeIds"/>. The set of
    /// ids must be exactly the current set (no additions/removals here — that is
    /// Add/RemoveNode's job); otherwise throws.</summary>
    public void ReorderNodes(string comboId, IReadOnlyList<string> orderedNodeIds)
    {
        ArgumentNullException.ThrowIfNull(orderedNodeIds);
        var session = GetSession(comboId);

        if (orderedNodeIds.Count != session.Nodes.Count)
            throw new ArgumentException("orderedNodeIds must list every current node exactly once", nameof(orderedNodeIds));

        var byId = session.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var reordered = new List<ComboNode>(session.Nodes.Count);
        foreach (var id in orderedNodeIds)
        {
            if (!byId.Remove(id, out var node))
                throw new ArgumentException($"orderedNodeIds contains unknown or duplicate id '{id}'", nameof(orderedNodeIds));
            reordered.Add(node);
        }

        session.Nodes.Clear();
        session.Nodes.AddRange(reordered);
    }

    /// <summary>Records a "hotkey string -> comboId" mapping in the persisted config, keyed per
    /// champion (see <see cref="SlotKeySeparator"/>) so this combo's own champion doesn't collide
    /// with another champion's combo already bound to the same raw hotkey. Does NOT register any
    /// OS hook or send any input (that is M13's job) — mapping only (P4 / Reviewer Checklist
    /// item 1).</summary>
    public void BindHotkey(string comboId, string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            throw new ArgumentException("hotkey must not be empty", nameof(hotkey));
        var session = GetSession(comboId); // validate the combo exists before binding
        _config.Set(HotkeyMapKeyPrefix + ComposeSlotKey(hotkey, session.ChampionId), comboId);
    }

    /// <summary>Builds the composite <c>hotkeys.comboSlots</c> value-segment key from a raw hotkey
    /// string and the champion it's bound for. Public so <c>Overlay.Client</c> call sites (e.g.
    /// deleting a combo's hotkey entries) can reconstruct the same key without duplicating the
    /// format.</summary>
    public static string ComposeSlotKey(string hotkey, string championId) => $"{hotkey}{SlotKeySeparator}{championId}";

    /// <summary>Splits a composite <c>hotkeys.comboSlots</c> key segment (as produced by
    /// <see cref="ComposeSlotKey"/>) back into its raw hotkey and championId. Uses the LAST
    /// occurrence of the separator, since neither a hotkey token (<see cref="HotkeyCombo.Parse"/>'s
    /// tokens are things like "CTRL"/"SHIFT"/a key name, joined with '+') nor a champion id ever
    /// contains "::". Backward-compat: a pre-upgrade key saved under the OLD flat format (no
    /// separator at all) has no champion segment — that entry degrades gracefully to
    /// <c>(compositeKey, "")</c> rather than throwing. Such legacy entries were orphaned after the
    /// composite-key upgrade; <see cref="MigrateLegacyHotkeyBindings"/> now recovers them at startup
    /// (rewrites them under the composite key from the bound combo's saved champion).</summary>
    public static (string Hotkey, string ChampionId) SplitSlotKey(string compositeKey)
    {
        int idx = compositeKey.LastIndexOf(SlotKeySeparator, StringComparison.Ordinal);
        return idx < 0
            ? (compositeKey, string.Empty)
            : (compositeKey[..idx], compositeKey[(idx + SlotKeySeparator.Length)..]);
    }

    /// <summary>One-time, idempotent migration of pre-upgrade (loop 44/45) hotkey bindings.
    /// Before the composite <c>{hotkey}::{championId}</c> key (see <see cref="ComposeSlotKey"/>),
    /// <c>hotkeys.comboSlots</c> was keyed by the raw hotkey alone (<c>{hotkey} -&gt; comboId</c>).
    /// Those flat entries have no champion dimension, so after the upgrade they can never
    /// fire-time-match a champion (<see cref="SplitSlotKey"/> degrades them to an empty championId)
    /// and were effectively orphaned — the user had to re-bind. This recovers them without a live
    /// game: for each flat entry, it looks up the bound combo's own <see cref="SavedCombo.ChampionId"/>
    /// (from <c>combos.saved.{comboId}</c>) and rewrites the binding under the composite key, then
    /// clears the old flat key. A flat entry whose combo no longer exists (dangling) is dropped.
    /// Composite keys already in the new format are left untouched, so calling this more than once
    /// (or on an already-migrated config) is a safe no-op. Returns the number of flat entries acted on.
    /// Call once at startup, BEFORE registering combo hotkeys.</summary>
    public int MigrateLegacyHotkeyBindings()
    {
        if (_config.Get("hotkeys.comboSlots") is not IDictionary<string, object?> slots)
            return 0;

        // Snapshot the flat entries first — do not mutate the config dict while enumerating it.
        var legacy = new List<(string FlatKey, string ComboId)>();
        foreach (var (key, value) in slots)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains(SlotKeySeparator, StringComparison.Ordinal))
                continue; // composite (already-migrated) or empty key — skip.
            var comboId = value?.ToString();
            if (string.IsNullOrWhiteSpace(comboId))
                continue; // null value = a dropped/deleted slot; nothing to migrate.
            legacy.Add((key, comboId));
        }

        int acted = 0;
        foreach (var (flatKey, comboId) in legacy)
        {
            string? championId = TryGetSavedComboChampionId(comboId);
            if (!string.IsNullOrWhiteSpace(championId))
            {
                // Rewrite under the composite key, then clear the flat one. If a composite entry
                // for this exact (hotkey, champion) already exists, this just rewrites the same
                // value — still correct and idempotent.
                _config.Set(HotkeyMapKeyPrefix + ComposeSlotKey(flatKey, championId!), comboId);
            }
            // Drop the orphaned flat key either way (migrated above, or dangling combo).
            _config.Set(HotkeyMapKeyPrefix + flatKey, null);
            acted++;
        }
        return acted;
    }

    /// <summary>Reads just the <see cref="SavedCombo.ChampionId"/> of a persisted combo, or null when
    /// the combo is absent or its stored JSON is corrupt. Deliberately does NOT open an edit session
    /// (unlike <see cref="LoadCombo"/>) — the migration only needs the champion id.</summary>
    private string? TryGetSavedComboChampionId(string comboId)
    {
        if (_config.Get(SavedComboKeyPrefix + comboId) is not string raw)
            return null;
        try
        {
            return JsonSerializer.Deserialize<SavedCombo>(raw, SavedComboOptions)?.ChampionId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Import / Export (spec Interfaces) ────────────────────────────────────────

    /// <summary>Serializes the combo to JSON by delegating to <see cref="ComboEngine.Serialize"/>
    /// (no bespoke JSON here).</summary>
    public string ExportCombo(string comboId)
    {
        var session = GetSession(comboId);
        return _engine.Serialize(Snapshot(session).Graph);
    }

    /// <summary>Validates the JSON schema (required fields present with correct types) and
    /// then delegates to <see cref="ComboEngine.Deserialize"/>. Malformed input throws a
    /// typed <see cref="ComboImportException"/> with a human-readable message instead of a
    /// raw <see cref="JsonException"/>/<see cref="NullReferenceException"/> (spec Agent
    /// Implementation Notes + Reviewer Checklist item 2). This does NOT create an editing
    /// session — it only parses/validates a graph; a caller wanting to edit the result can
    /// feed its nodes into a new <see cref="CreateCombo"/>+<see cref="AddNode"/> flow.</summary>
    public ComboGraph ImportCombo(string json)
    {
        ValidateComboJson(json);
        try
        {
            return _engine.Deserialize(json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new ComboImportException("Combo JSON could not be deserialized: " + ex.Message, ex);
        }
    }

    // ── Persistence + save event ─────────────────────────────────────────────────

    /// <summary>Persists the combo (as a serialized <see cref="SavedCombo"/>) via M14 and
    /// publishes <c>UI.COMBO_SAVED</c> on the M15 bus (spec Internal Logic #4).</summary>
    public void SaveCombo(string comboId)
    {
        var session = GetSession(comboId);
        var graphJson = _engine.Serialize(Snapshot(session).Graph);
        var saved = new SavedCombo(session.Id, session.ChampionId, session.Name, graphJson);

        _config.Set(SavedComboKeyPrefix + session.Id, JsonSerializer.Serialize(saved, SavedComboOptions));

        EventBus.EventBus.Publish(
            "UI.COMBO_SAVED",
            new ComboSavedPayload(session.Id, session.ChampionId, session.Name),
            source: "M04.ComboEditor");
    }

    /// <summary>Loads a previously <see cref="SaveCombo"/>d combo back into a live editing
    /// session and returns its snapshot. Throws if no combo is stored under
    /// <paramref name="comboId"/>.</summary>
    public ComboDraft LoadCombo(string comboId)
    {
        if (_config.Get(SavedComboKeyPrefix + comboId) is not string raw)
            throw new KeyNotFoundException($"No saved combo with id '{comboId}'");

        var saved = JsonSerializer.Deserialize<SavedCombo>(raw, SavedComboOptions)
            ?? throw new ComboImportException($"Saved combo '{comboId}' is corrupt");
        var graph = _engine.Deserialize(saved.GraphJson);

        var session = new EditSession(saved.Id, saved.ChampionId, saved.Name);
        session.Nodes.AddRange(graph.Nodes);
        _sessions[saved.Id] = session;
        return new ComboDraft(saved.Id, saved.ChampionId, saved.Name, graph);
    }

    // ── Preview (spec Internal Logic #3) ─────────────────────────────────────────

    /// <summary>Rebuilds the graph and runs the M03 engine so a UI can show live
    /// damage/mana/killangle. Pure reuse of M03/M05 — no duplicated math (Reviewer
    /// Checklist item 3).</summary>
    public ComboResult PreviewCombo(string comboId, ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var session = GetSession(comboId);
        var graph = _engine.BuildGraph(session.Nodes);
        return _engine.Execute(graph, context);
    }

    /// <summary>Returns the current snapshot of an in-progress combo.</summary>
    public ComboDraft GetCombo(string comboId) => Snapshot(GetSession(comboId));

    // ── NodePalette (spec Internal Logic #1) ─────────────────────────────────────

    /// <summary>Loads a champion's P/Q/W/E/R skills (from M11) plus a default auto-attack
    /// node as starting <see cref="ComboNode"/>s the user can add to a combo and then
    /// customize. Skill damage/ratios are left at 0 — this palette is a template of
    /// cooldown/mana/name/type defaults, not a pre-computed damage table (damage is M03/M05's
    /// job, and BIN spell coefficients are formula trees, not flat ratios). Throws if the
    /// champion is not loaded.</summary>
    /// <summary>Ability order for the palette: the four abilities in cast order after the passive,
    /// with anything else last.</summary>
    private static int BaseSlotRank(string slot) => slot.Length == 0 ? 5 : slot[0] switch
    {
        'P' => 0,
        'Q' => 1,
        'W' => 2,
        'E' => 3,
        'R' => 4,
        _ => 5,
    };

    /// <summary>Reorders a palette so each variant slot follows its base ability — Q, Q2, Q3,
    /// QRhaast, then W… — instead of every variant being appended after R.
    ///
    /// <para>A variant's slot key always starts with the letter of the ability it varies (RWall is
    /// still R, QCalibrum is still Q), which is the same convention the icon fallback relies on. The
    /// sort is STABLE, so within one ability the canonical slot stays first and the curation file's
    /// own order decides the rest.</para></summary>
    private static List<ComboNode> GroupVariantsWithTheirBase(List<ComboNode> nodes)
        => nodes
            .OrderBy(n => BaseSlotRank(n.Id))
            .ThenBy(n => n.Id.Length == 1 ? 0 : 1)
            .ToList();

    public static NodePalette LoadPalette(string championId)
    {
        if (string.IsNullOrWhiteSpace(championId))
            throw new ArgumentException("championId must not be empty", nameof(championId));
        if (ChampionRepository.Get(championId) is null)
            throw new KeyNotFoundException($"Champion '{championId}' is not loaded");

        var nodes = new List<ComboNode>();
        foreach (var slot in new[] { "P", "Q", "W", "E", "R" })
        {
            var skill = ChampionRepository.GetSkill(championId, slot);
            if (skill is null) continue;
            nodes.Add(new ComboNode(
                Id: slot,
                NodeType: slot == "P" ? ComboNodeType.Passive : ComboNodeType.Skill,
                Name: skill.Name,
                Cooldown: skill.Cooldown.Length > 0 ? skill.Cooldown[0] : 0,
                Mana: skill.Mana.Length > 0 ? skill.Mana[0] : 0,
                Damage: 0,
                DamageType: MapDamageType(skill.DamageType),
                RatioAD: 0,
                RatioBonusAD: 0,
                RatioAP: 0,
                CastTime: 0,
                Delay: 0,
                TravelTime: 0));
        }

        // (M22) Extra multi-form skills: any curated slot beyond the canonical P/Q/W/E/R (e.g. Jayce
        // "QCannon"/"WCannon", a transform/stance/weapon/sub-spell) is offered as a selectable node so
        // the user can build combos mixing forms. Damage type comes from the slot's first curated hit;
        // the number itself is still computed live at combo time (template Damage stays 0). Slot keys
        // are underscore-free by convention so the combo runner's SlotOf recovers them from a
        // "{slot}_{n}" sequence id.
        var canonical = new HashSet<string>(StringComparer.Ordinal) { "P", "Q", "W", "E", "R" };
        foreach (var slot in SkillDamageDb.GetCuratedSlotKeys(championId))
        {
            if (canonical.Contains(slot)) continue;
            var hits = SkillDamageDb.GetHits(championId, slot);
            var type = hits is { Length: > 0 } ? MapHitDamageType(hits[0].Type) : ComboDamageType.Physical;
            nodes.Add(new ComboNode(
                Id: slot,
                NodeType: ComboNodeType.Skill,
                Name: PrettyExtraSlotName(slot),
                Cooldown: 0,
                Mana: 0,
                Damage: 0,
                DamageType: type,
                RatioAD: 0,
                RatioBonusAD: 0,
                RatioAP: 0,
                CastTime: 0,
                Delay: 0,
                TravelTime: 0));
        }

        // (loop 478) Put every variant next to the ability it varies. Until now the canonical
        // P/Q/W/E/R came first and every extra slot was appended after R, so Aatrox read
        // P Q W R Q2 Q3 and Kayn read Q W R QRhaast RRhaast — the reader had to hunt for a cast's
        // other forms at the far end of the palette. Ordering is stable within a base, so the
        // curation file's own order still decides which variant comes first.
        nodes = GroupVariantsWithTheirBase(nodes);

        nodes.Add(new ComboNode(
            Id: "AA",
            NodeType: ComboNodeType.Aa,
            Name: "Auto Attack",
            Cooldown: 0,
            Mana: 0,
            Damage: 0,
            DamageType: ComboDamageType.Physical,
            RatioAD: 0,
            RatioBonusAD: 0,
            RatioAP: 0,
            CastTime: 0,
            Delay: 0,
            TravelTime: 0));

        return new NodePalette(championId, nodes);
    }

    // ── Internals ────────────────────────────────────────────────────────────────

    private EditSession GetSession(string comboId)
    {
        if (string.IsNullOrWhiteSpace(comboId))
            throw new ArgumentException("comboId must not be empty", nameof(comboId));
        if (!_sessions.TryGetValue(comboId, out var session))
            throw new KeyNotFoundException($"No combo with id '{comboId}'");
        return session;
    }

    private static int IndexOfNode(EditSession session, string nodeId)
    {
        int index = session.Nodes.FindIndex(n => n.Id == nodeId);
        if (index < 0) throw new KeyNotFoundException($"No node with id '{nodeId}' in combo '{session.Id}'");
        return index;
    }

    /// <summary>Builds the ComboGraph snapshot for a session. An empty session yields an
    /// empty graph; a non-empty one is built (and validated) via
    /// <see cref="ComboEngine.BuildGraph"/> so the editor's graph and the engine's graph are
    /// literally the same construction.</summary>
    private ComboDraft Snapshot(EditSession session)
    {
        var graph = session.Nodes.Count == 0
            ? new ComboGraph(Array.Empty<ComboNode>(), Array.Empty<ComboEdge>())
            : _engine.BuildGraph(session.Nodes);
        return new ComboDraft(session.Id, session.ChampionId, session.Name, graph);
    }

    private static ComboDamageType MapDamageType(DamageType type) => type switch
    {
        DamageType.PHYSICAL => ComboDamageType.Physical,
        DamageType.MAGIC => ComboDamageType.Magic,
        DamageType.TRUE => ComboDamageType.True,
        // ComboDamageType has no Mixed member; a palette node is a template the user
        // customizes, so MIXED falls back to Physical as its editable starting type.
        DamageType.MIXED => ComboDamageType.Physical,
        _ => ComboDamageType.Physical,
    };

    /// <summary>(M22) Maps a curated per-hit <see cref="HitDamageType"/> to the editor node's
    /// <see cref="ComboDamageType"/> for the palette entry of an extra multi-form slot.</summary>
    private static ComboDamageType MapHitDamageType(HitDamageType type) => type switch
    {
        HitDamageType.Physical => ComboDamageType.Physical,
        HitDamageType.Magic => ComboDamageType.Magic,
        HitDamageType.True => ComboDamageType.True,
        _ => ComboDamageType.Physical,
    };

    /// <summary>(M22) Human-readable palette name for an extra multi-form slot key. Splits the
    /// underscore-free camel/form key into a base token plus a parenthesised qualifier, e.g.
    /// "QCannon" → "Q (Cannon)", "WCannon" → "W (Cannon)". Falls back to the raw key when it does
    /// not start with a Q/W/E/R/P base letter.</summary>
    private static string PrettyExtraSlotName(string slot)
    {
        if (slot.Length > 1 && "PQWER".IndexOf(slot[0]) >= 0)
        {
            // (loop 478) A numbered cast is already the name people use — Q2, Q3 — so it is left
            // alone rather than rendered as "Q (2)". Only a word qualifier gets the parentheses.
            var qualifier = slot[1..];
            return qualifier.All(char.IsDigit) ? slot : $"{slot[0]} ({qualifier})";
        }
        return slot;
    }

    // ── Import schema validation ─────────────────────────────────────────────────

    private static readonly string[] RequiredNumericFields =
    {
        "Cooldown", "Mana", "Damage", "RatioAD", "RatioBonusAD", "RatioAP",
        "CastTime", "Delay", "TravelTime",
    };

    /// <summary>Validates that <paramref name="json"/> is a well-formed combo graph before it
    /// reaches <see cref="ComboEngine.Deserialize"/>: root object with a non-empty "Nodes"
    /// array, each node carrying the required fields with the correct JSON types. System.Text.Json
    /// would otherwise silently default missing required fields (positional-record parameters),
    /// so this check is what turns "malformed input" into a clear <see cref="ComboImportException"/>
    /// rather than a wrong-but-silent graph or an opaque crash.</summary>
    private static void ValidateComboJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ComboImportException("Combo JSON is empty.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ComboImportException("Combo JSON is not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ComboImportException("Combo JSON root must be an object.");

            if (!root.TryGetProperty("Nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                throw new ComboImportException("Combo JSON must contain a 'Nodes' array.");
            if (nodes.GetArrayLength() == 0)
                throw new ComboImportException("Combo JSON 'Nodes' array must not be empty.");

            int i = 0;
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object)
                    throw new ComboImportException($"Combo JSON node[{i}] must be an object.");

                RequireNonEmptyString(node, "Id", i);
                RequireString(node, "Name", i);
                RequireEnum<ComboNodeType>(node, "NodeType", i);
                RequireEnum<ComboDamageType>(node, "DamageType", i);
                foreach (var field in RequiredNumericFields)
                    RequireNumber(node, field, i);

                i++;
            }
        }
    }

    private static void RequireString(JsonElement node, string field, int index)
    {
        if (!node.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ComboImportException($"Combo JSON node[{index}] is missing required string field '{field}'.");
    }

    private static void RequireNonEmptyString(JsonElement node, string field, int index)
    {
        if (!node.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ComboImportException($"Combo JSON node[{index}] is missing required non-empty string field '{field}'.");
    }

    private static void RequireNumber(JsonElement node, string field, int index)
    {
        if (!node.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number)
            throw new ComboImportException($"Combo JSON node[{index}] is missing required numeric field '{field}'.");
    }

    private static void RequireEnum<TEnum>(JsonElement node, string field, int index) where TEnum : struct, Enum
    {
        if (!node.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String
            || !Enum.TryParse<TEnum>(value.GetString(), ignoreCase: false, out _))
            throw new ComboImportException($"Combo JSON node[{index}] has a missing or invalid '{field}' value.");
    }

    /// <summary>Mutable per-combo editing state. Not exposed directly — callers see the
    /// immutable <see cref="ComboDraft"/> snapshot instead.</summary>
    private sealed class EditSession
    {
        public string Id { get; }
        public string ChampionId { get; }
        public string Name { get; set; }
        public List<ComboNode> Nodes { get; } = new();

        public EditSession(string id, string championId, string name)
        {
            Id = id;
            ChampionId = championId;
            Name = name;
        }
    }
}

/// <summary>Immutable snapshot of an editing session: the addressable <see cref="Id"/>,
/// its <see cref="ChampionId"/>/<see cref="Name"/>, and the current <see cref="Graph"/>
/// (the spec's <c>createCombo -&gt; ComboGraph</c> value, reachable via <see cref="Graph"/>).</summary>
public sealed record ComboDraft(string Id, string ChampionId, string Name, ComboGraph Graph);

/// <summary>M04 auxiliary model (spec Data Model): a champion's loadable starting nodes.</summary>
public sealed record NodePalette(string ChampionId, IReadOnlyList<ComboNode> AvailableNodes);

/// <summary>Persisted shape of a saved combo (stored as a JSON string under
/// <c>combos.saved.{id}</c> via M14). <see cref="GraphJson"/> is the exact
/// <see cref="ComboEngine.Serialize"/> output, so a load round-trips identically.</summary>
public sealed record SavedCombo(string Id, string ChampionId, string Name, string GraphJson);

/// <summary>Payload published on <c>UI.COMBO_SAVED</c> when a combo is saved.</summary>
public sealed record ComboSavedPayload(string ComboId, string ChampionId, string Name);

/// <summary>Thrown when <see cref="ComboEditor.ImportCombo"/> receives JSON that is not a
/// valid combo graph (bad JSON, wrong root type, missing/typed-wrong required node fields),
/// so a UI can show a clear validation message instead of crashing (spec Agent
/// Implementation Notes).</summary>
public sealed class ComboImportException : Exception
{
    public ComboImportException(string message) : base(message) { }
    public ComboImportException(string message, Exception inner) : base(message, inner) { }
}
