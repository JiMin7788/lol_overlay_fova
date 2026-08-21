using System.Text.Json.Serialization;

namespace Overlay.Core.Config;

/// <summary>
/// M14 Data Model: the persisted user configuration, exactly matching the spec's
/// Data Model section (overlay/hotkeys/voice/general) plus a <see cref="SchemaVersion"/>
/// field (spec Internal Logic #3: "스키마 버전 필드를 두어, 향후 스키마 변경 시 마이그레이션
/// 함수 실행"). There is only one schema version today, so
/// <see cref="ConfigMigrator"/> is a no-op hook, not a real migration chain.
///
/// Unknown top-level or nested JSON properties are silently ignored by
/// System.Text.Json's default deserialization (it does not throw on unmapped
/// properties), which is exactly the Acceptance Criteria #2 requirement
/// ("스키마에 없는 알 수 없는 키가 파일에 있어도 앱이 크래시하지 않고 무시한다").
/// </summary>
public sealed class ConfigSchema
{
    /// <summary>Current schema version this class corresponds to. Bump when the shape
    /// of <see cref="ConfigSchema"/> changes and add a step in <see cref="ConfigMigrator"/>.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("overlay")]
    public OverlayConfig Overlay { get; set; } = new();

    [JsonPropertyName("hotkeys")]
    public HotkeysConfig Hotkeys { get; set; } = new();

    [JsonPropertyName("voice")]
    public VoiceConfig Voice { get; set; } = new();

    [JsonPropertyName("general")]
    public GeneralConfig General { get; set; } = new();

    [JsonPropertyName("combos")]
    public CombosConfig Combos { get; set; } = new();

    [JsonPropertyName("targeting")]
    public TargetingConfig Targeting { get; set; } = new();

    [JsonPropertyName("runes")]
    public RunesConfig Runes { get; set; } = new();

    [JsonPropertyName("items")]
    public ItemsConfig Items { get; set; } = new();

    [JsonPropertyName("targetSnapshots")]
    public TargetSnapshotsConfig TargetSnapshots { get; set; } = new();

    /// <summary>M31 minimap-vision capture settings (kill switch + fps).</summary>
    [JsonPropertyName("minimap")]
    public MinimapConfig Minimap { get; set; } = new();

    /// <summary>M29 HOME-window ad slot (kill switch + creative endpoint).</summary>
    [JsonPropertyName("ads")]
    public AdsConfig Ads { get; set; } = new();

    /// <summary>Opt-in diagnostics/benchmark hooks — all off by default. Additive section,
    /// same convention as "ads"/"minimap".</summary>
    [JsonPropertyName("diagnostics")]
    public DiagnosticsConfig Diagnostics { get; set; } = new();

    /// <summary>M33 champ-select assistant (LCU rune/spell presets). Additive section.</summary>
    [JsonPropertyName("champSelect")]
    public ChampSelectConfig ChampSelect { get; set; } = new();

    /// <summary>Deep clone used so <see cref="ConfigManager.Reset"/> can restore fresh
    /// default instances without callers accidentally sharing mutable nested objects.</summary>
    public static ConfigSchema CreateDefault() => new();
}

/// <summary>Opt-in diagnostics hooks. <see cref="FrameDropMeter"/> arms the M16 frame-drop
/// benchmark on the overlay window (writes <c>logs/framedrop.log</c>) — measurement sessions
/// only, never on by default.</summary>
public sealed class DiagnosticsConfig
{
    [JsonPropertyName("frameDropMeter")]
    public bool FrameDropMeter { get; set; } = false;
}

/// <summary>M33 champ-select assistant config. <see cref="AutoApply"/> is the P4-critical
/// standing opt-in for applying preset[0] on lock WITHOUT a per-game click — default false;
/// the default flow is one explicit click per application. <see cref="Presets"/> mirrors the
/// <see cref="CombosConfig.Saved"/> string-dictionary precedent: numeric champion key ->
/// serialized <c>RunePreset[]</c> JSON, giving <c>champSelect.presets.{key}</c> a schema home
/// so the typed round-trip preserves it.</summary>
public sealed class ChampSelectConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("autoApply")]
    public bool AutoApply { get; set; } = false;

    [JsonPropertyName("presets")]
    public Dictionary<string, string> Presets { get; set; } = new();

    /// <summary>M33 D4 phase-2 (local form): directory of aggregated recommendation files
    /// (<c>rec/{patch}/{championKey}.json</c>, produced by tools/aggregate_runes.py). Empty
    /// (default) = no recommendation source; the panel shows local presets only.</summary>
    [JsonPropertyName("recDir")]
    public string RecDir { get; set; } = "";
}

/// <summary>M29 ad-slot config. Additive section — old configs missing "ads" deserialize to these
/// defaults. <see cref="Endpoint"/> is empty until the creative server exists, and an empty endpoint
/// means the slot reserves its space but shows nothing (M29 §2 failure posture: collapse, never a
/// spinner or an error). <see cref="Enabled"/> is the ad-free flag a supporter tier would flip.</summary>
public sealed class AdsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>HTTPS URL returning the ad manifest (image + click URLs). Empty = no ads.</summary>
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = "";
}

/// <summary>M31 minimap-vision capture config. Additive section — old configs missing "minimap"
/// deserialize to these defaults (no schema-version bump needed, same as prior additive keys).</summary>
public sealed class MinimapConfig
{
    /// <summary>M31 §5 KILL SWITCH. <b>Default ON</b> (loop 165, user decision "스위치 on 및 모듈
    /// 활성화" — capture is activated so the live-game mode matrix + §5 perf gate can be measured).
    /// NOTE: capture now runs by default; before public release this must be reconciled with the
    /// §8 Riot-submission gate (CLAUDE_CODE_TODO §38-F/I) — flip back to false if the submission
    /// text hasn't landed.</summary>
    [JsonPropertyName("vision")]
    public bool Vision { get; set; } = true;

    /// <summary>M31 §3/§9-2 prefilter cap. Default 30 fps; 60 is the opt-in "high sensitivity"
    /// mode. Treated as a runtime parameter (clamped 1..60 by the capture source).</summary>
    [JsonPropertyName("captureFps")]
    public int CaptureFps { get; set; } = 30;

    /// <summary>§43-D — how long an enemy must go undetected before "사라짐" is announced, in ms.
    /// Default 2500.
    ///
    /// <para>This is latency the user feels directly, and it is a WORKAROUND, not a feature: it was
    /// raised from 1000 to 2500 to stop a brief detection gap from firing a false disappear, which
    /// added 1.5s to every genuine one. Exposed here so it can be lowered as detection reliability
    /// improves, without a rebuild. Lower = faster alerts and more false ones; higher = the
    /// reverse.</para></summary>
    [JsonPropertyName("lostDebounceMs")]
    public double LostDebounceMs { get; set; } = 2500;

    /// <summary>§43 — retain the last ~2s of captured minimap frames in memory so a wrong callout
    /// can be dumped to PNG afterwards (hotkey <see cref="DebugDumpHotkey"/>).
    ///
    /// <para>Default OFF because it costs about 19 MB of RAM while on. Deliberately a RING rather
    /// than continuous PNG writing: encoding every frame is affordable on CPU (~2-5 ms per 280×280)
    /// but writes roughly 150 MB per minute to disk at 30fps, which is not something to leave
    /// running. With the ring, the steady-state cost is one ~313 KB array copy per frame and PNG
    /// encoding happens only on the explicit dump.</para></summary>
    [JsonPropertyName("debugCapture")]
    public bool DebugCapture { get; set; }

    /// <summary>§43 — hotkey that writes the retained frames to <c>logs/minimap-debug/</c>. Only
    /// registered while <see cref="DebugCapture"/> is on.</summary>
    [JsonPropertyName("debugDumpHotkey")]
    public string DebugDumpHotkey { get; set; } = "Alt+3";

    /// <summary>M31 §C minimap afterimage (last-seen champion portrait at 50% opacity).</summary>
    [JsonPropertyName("afterimage")]
    public AfterimageConfig Afterimage { get; set; } = new();
}

/// <summary>M31 §C/§D — the minimap "afterimage" marker: when an enemy drops out of vision a
/// half-transparent portrait of that champion stays at the last-seen spot until they are seen
/// again. Master switch plus a per-role gate, so a user who only cares about the enemy jungler
/// can silence the other four.</summary>
public sealed class AfterimageConfig
{
    /// <summary>Master switch for the afterimage marker. Default ON.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Portrait opacity, 0.0-1.0. Default 0.75 (raised from the originally requested 0.5
    /// on 2026-07-20 — in a live game 50% read as too faint, especially while the marker was also
    /// being drawn grayscale). Kept configurable since it is a readability preference over the
    /// live minimap art.</summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.75;

    /// <summary>Per-role gates, keyed "top"/"jungle"/"mid"/"adc"/"support". A role missing from
    /// the map counts as enabled, so old configs keep every marker. An alert whose RoleKey is
    /// empty (role unresolved) obeys <see cref="Enabled"/> alone — there is no gate to consult.</summary>
    [JsonPropertyName("roles")]
    public Dictionary<string, bool> Roles { get; set; } = new();
}

public sealed class OverlayConfig
{
    /// <summary>HUD display duration in seconds. Spec-mandated default: 4.</summary>
    [JsonPropertyName("hudDisplayDuration")]
    public double HudDisplayDuration { get; set; } = 4;

    [JsonPropertyName("position")]
    public PositionConfig Position { get; set; } = new();

    /// <summary>Overlay opacity, 0.0-1.0. Not specified by the spec; documented default
    /// chosen here: 1.0 (fully opaque) — see Agent report Notes for Reviewer.</summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    /// <summary>Overlay HUD size scale (M19 Settings → 오버레이 크기). Default 1.0.</summary>
    [JsonPropertyName("scale")]
    public double Scale { get; set; } = 1.0;

    /// <summary>Overlay movable mode (M19 이동 여부): true = interactive/draggable
    /// (click-through off), false = click-through in-game (default).</summary>
    [JsonPropertyName("movable")]
    public bool Movable { get; set; }

    /// <summary>Global hotkey that toggles overlay visibility (M19 §1). Default SHIFT+TAB
    /// (rebindable; SHIFT+TAB overlaps LoL's scoreboard modifier — user may change it).</summary>
    [JsonPropertyName("toggleHotkey")]
    public string ToggleHotkey { get; set; } = "SHIFT+TAB";

    /// <summary>Index of the monitor the overlay covers when no game window is tracked.</summary>
    [JsonPropertyName("targetMonitor")]
    public int TargetMonitor { get; set; }

    /// <summary>Modifier key that must be held down for the combo-result HUD card's target
    /// portrait to become clickable (M02 loop 38 continuation 12 — click-to-target, replacing the
    /// Settings-dropdown target picker). The overlay stays click-through the rest of the time;
    /// only this exact modifier being held clears WS_EX_TRANSPARENT. Default Control (rebindable,
    /// like <see cref="ToggleHotkey"/>). Holds a single
    /// <see cref="Overlay.Core.Hotkeys.HotkeyModifiers"/> name ("Control"/"Alt"/"Shift"/"Win"),
    /// parsed with <c>Enum.TryParse</c> by the consumer — not a full <c>HotkeyCombo</c> string
    /// (which requires a non-modifier key and would reject a bare modifier name).</summary>
    [JsonPropertyName("targetClickModifier")]
    public string TargetClickModifier { get; set; } = "Control";

    /// <summary>(loop 130) The user's own lane for the same-lane enemy RETURN timer's travel distance:
    /// "auto" (use the Live Client position — only reliable in ranked/draft), or "top"/"mid"/"bot"/"jungle"
    /// to override. Needed because the Live Client omits positions in most games, which made every lane
    /// use the fallback (side-lane) distance. Default "auto".</summary>
    [JsonPropertyName("laneReturnLane")]
    public string LaneReturnLane { get; set; } = "auto";

    /// <summary>(loop 141) Which enemy deaths get a return timer: "all" (every enemy champion), "designated"
    /// (only the ⇄-pinned target), or "sameLane" (only a same-lane enemy). Default "all".</summary>
    [JsonPropertyName("laneReturnMode")]
    public string LaneReturnMode { get; set; } = "all";

    /// <summary>Per-item enable toggles for the overlay item catalog (M19 §3), extended by
    /// M02 pending-change #1 to cover every HUD element type (see <see cref="OverlayItemsConfig"/>).</summary>
    [JsonPropertyName("items")]
    public OverlayItemsConfig Items { get; set; } = new();

    /// <summary>M02 pending-change #1 (modular per-element HUD positioning): a per-HUD-type
    /// position DELTA in overlay-window DIPs, keyed by the same short names as
    /// <see cref="OverlayItemsConfig"/> (e.g. "comboResult", "inhibitorTimers", "statusCard").
    /// Each entry is an OFFSET added to that element's default computed anchor, not an
    /// absolute screen position — set live by dragging the element on the overlay while
    /// <see cref="Movable"/> is on (<c>OverlayHost</c>'s per-element drag). A missing key
    /// means "use the default anchor unchanged" (offset 0,0). Replaces the old single
    /// <see cref="Position"/>/<see cref="Movable"/> "move everything as one block" model —
    /// <see cref="Position"/> above is now used only as the Config-default fallback anchor
    /// for HUD payloads that don't specify one (unrelated to this per-type map).</summary>
    [JsonPropertyName("positions")]
    public Dictionary<string, PositionConfig> Positions { get; set; } = new();

    /// <summary>One-time minimap calibration for the minimap-anchored timers (inhibitor / Nexus-turret /
    /// enemy-return). The Live Client API and game.cfg cannot yield the minimap's exact on-screen pixel
    /// rect (MinimapScale→pixel is undocumented), so the user aligns a box over their minimap ONCE in
    /// movable mode; it persists here and all minimap timers place against it thereafter. When
    /// <see cref="MinimapCalibrationConfig.Enabled"/> is false the overlay falls back to the geometric /
    /// game.cfg auto estimate.</summary>
    [JsonPropertyName("minimapCalibration")]
    public MinimapCalibrationConfig MinimapCalibration { get; set; } = new();

    /// <summary>Minimap-position calibration MODE toggle (client setting, default OFF). When on AND the
    /// overlay is movable, the cyan minimap box + hint show so the user can drag/wheel-align it; off means
    /// the calibration UI never appears in-game (the saved <see cref="MinimapCalibration"/> still applies).</summary>
    [JsonPropertyName("minimapCalibrate")]
    public bool MinimapCalibrate { get; set; }
}

/// <summary>One-time user-aligned minimap rect, stored as fractions of the overlay window so it survives
/// resolution changes reasonably. The minimap is square: <see cref="X"/>/<see cref="Y"/> are the
/// top-left as fractions of window width/height; <see cref="Size"/> is the edge as a fraction of window
/// HEIGHT. Set by dragging (position) + mouse-wheel (size) the calibration box in movable mode.</summary>
public sealed class MinimapCalibrationConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("size")]
    public double Size { get; set; }
}

/// <summary>Enable flags for every HUD element type the M02 Overlay Engine can show. The
/// original M19 §3 items (Inhibitor/Global Gold) are opt-in engine items and default
/// DISABLED; the M02 pending-change #1 additions below cover the always-on core HUD elements
/// and default ENABLED so pre-existing behavior (always shown) is unchanged unless the user
/// explicitly opts out.</summary>
public sealed class OverlayItemsConfig
{
    /// <summary>Inhibitor respawn timer (M19 §3.2). Default ENABLED (loop 71, user request) — it is
    /// real API-backed data (InhibKilled → 5:00 respawn), so it is safe to show by default.</summary>
    [JsonPropertyName("inhibitorTimers")]
    public ItemToggleConfig InhibitorTimers { get; set; } = new() { Enabled = true };

    /// <summary>Nexus ("twin") turret respawn timer (patch 15.1: destroyed Nexus turrets respawn
    /// after 3:00). Default ENABLED — real API-backed data (TurretKilled → 3:00 respawn), same
    /// honesty basis as <see cref="InhibitorTimers"/>.</summary>
    [JsonPropertyName("nexusTurretTimers")]
    public ItemToggleConfig NexusTurretTimers { get; set; } = new() { Enabled = true };

    [JsonPropertyName("globalGold")]
    public ItemToggleConfig GlobalGold { get; set; } = new();

    /// <summary>(loop 125) Same-lane enemy RETURN timer (minimap-left portrait + countdown). Default
    /// ENABLED. The respawn part is exact (API), the travel part is a move-speed ESTIMATE (marked "~").</summary>
    [JsonPropertyName("laneReturn")]
    public ItemToggleConfig LaneReturn { get; set; } = new() { Enabled = true };

    /// <summary>M30 Enemy Jungler Spotted Alert. Default ENABLED — real API field diffs (CS/item
    /// change on the enemy JUNGLE-position row), no coordinate estimate, same honesty basis as
    /// <see cref="InhibitorTimers"/>. Silently inert outside ranked/draft (no `position` data).</summary>
    [JsonPropertyName("enemyJunglerSpotted")]
    public ItemToggleConfig EnemyJunglerSpotted { get; set; } = new() { Enabled = true };

    /// <summary>M02 pending-change #1: combo-result HUD card. Default enabled (opt-out).</summary>
    [JsonPropertyName("comboResult")]
    public ItemToggleConfig ComboResult { get; set; } = new() { Enabled = true };

    /// <summary>M02 pending-change #1. Default enabled (opt-out).</summary>
    [JsonPropertyName("itemAlert")]
    public ItemToggleConfig ItemAlert { get; set; } = new() { Enabled = true };

    /// <summary>Enemy legendary-item-completed alert (<see cref="Overlay.Core.Items.EnemyItemAlertDetector"/>).
    /// Default ENABLED — real API item-list diffs on enemy rows (an enemy's data only refreshes while
    /// visible), same honesty basis as <see cref="EnemyJunglerSpotted"/>. Shows the enemy champion
    /// portrait + the completed item's icon.</summary>
    [JsonPropertyName("enemyItemAlert")]
    public ItemToggleConfig EnemyItemAlert { get; set; } = new() { Enabled = true };

    /// <summary>(§40) The always-on enemy PORTRAIT ROW above the combo overlay (click a portrait to
    /// select the target; dead enemies grey out + show a respawn countdown). Default ENABLED.</summary>
    [JsonPropertyName("enemyPortraitRow")]
    public ItemToggleConfig EnemyPortraitRow { get; set; } = new() { Enabled = true };

    /// <summary>(§40) The per-skill (P/Q/W/E/R/A) damage overlay, toggled by its hotkey (Alt+2 default).
    /// Default DISABLED (opt-in) — the combo-damage overlay is the primary view.</summary>
    [JsonPropertyName("skillOverlay")]
    public ItemToggleConfig SkillOverlay { get; set; } = new() { Enabled = false };

    /// <summary>M02 pending-change #1. Default enabled (opt-out).</summary>
    [JsonPropertyName("recallTimer")]
    public ItemToggleConfig RecallTimer { get; set; } = new() { Enabled = true };

    /// <summary>M02 pending-change #1. Default enabled (opt-out).</summary>
    [JsonPropertyName("notification")]
    public ItemToggleConfig Notification { get; set; } = new() { Enabled = true };

    /// <summary>M02 pending-change #1: the persistent in-game status card
    /// (<c>OverlayHost.BuildStatusCard</c>) is not a <see cref="Overlay.Core.Overlay.HudType"/> —
    /// it renders directly every frame, outside the coordinator — but is still one of the "HUD
    /// element types" the pending change asks to be individually toggle-able/positionable.
    /// Default enabled (opt-out).</summary>
    [JsonPropertyName("statusCard")]
    public ItemToggleConfig StatusCard { get; set; } = new() { Enabled = true };
}

public sealed class ItemToggleConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

public sealed class PositionConfig
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }
}

public sealed class HotkeysConfig
{
    /// <summary>Slot id -> key binding string (e.g. "Q", "Ctrl+1"). Empty by default;
    /// no default bindings are specified by the spec.</summary>
    [JsonPropertyName("comboSlots")]
    public Dictionary<string, string> ComboSlots { get; set; } = new();
}

public sealed class VoiceConfig
{
    /// <summary>Documented default: false (opt-in, avoids unexpected audio on first run).</summary>
    [JsonPropertyName("ttsEnabled")]
    public bool TtsEnabled { get; set; }

    /// <summary>0.0-1.0. Documented default: 0.8.</summary>
    [JsonPropertyName("ttsVolume")]
    public double TtsVolume { get; set; } = 0.8;

    /// <summary>Documented default: "default" (placeholder pack id; M09 TTS Engine owns
    /// the actual pack catalog).</summary>
    [JsonPropertyName("voicePack")]
    public string VoicePack { get; set; } = "default";

    /// <summary>Documented default: false (opt-in, matches ttsEnabled's rationale).</summary>
    [JsonPropertyName("sttEnabled")]
    public bool SttEnabled { get; set; }

    /// <summary>Minimum seconds between TTS callouts (M09 TtsScheduler throttle). Default
    /// 5 matches the scheduler's built-in fallback; this gives the tunable a persisted home.</summary>
    [JsonPropertyName("ttsCooldownSeconds")]
    public double TtsCooldownSeconds { get; set; } = 5;

    /// <summary>M31 §B — how enemy-presence alerts are voiced. <c>"prerecorded"</c> plays the bundled
    /// clips; ANY other value silences them, because <c>EnemyVoicePlayer</c> gates on this being
    /// exactly "prerecorded". Default "prerecorded".
    ///
    /// <para>An earlier version of this comment promised that <c>"tts"</c> falls back to the runtime
    /// synthesizer. It does not — that fallback was never implemented, so "tts" behaves identically to
    /// "off". The settings view therefore offers on/off only, rather than surfacing a mode that would
    /// silently do nothing.</para>
    ///
    /// <para>Scoped to <c>UI.ENEMY_PRESENCE</c> only — other TTS is unaffected.</para></summary>
    [JsonPropertyName("enemyVoicePack")]
    public string EnemyVoicePack { get; set; } = "prerecorded";

    /// <summary>M31 §B — location granularity for enemy-presence voice. <c>"simple"</c> names a
    /// broad zone ("적 캠프", "탑라인"); <c>"detail"</c> names the nearest camp/objective
    /// ("적 레드", "윗 바위게"). Default "simple": it is the coarser, lower-risk mode, and detail
    /// mode's camp anchors are still v1 approximations pending a live pass.</summary>
    [JsonPropertyName("enemyVoiceDetail")]
    public string EnemyVoiceDetail { get; set; } = "simple";

    /// <summary>M31 §B — playback volume for the enemy-presence clips, 0.0-1.0. Default 1.0 (the
    /// clips play at their recorded level).
    ///
    /// <para>Applied as a linear gain on the spliced PCM, not on the player: the prerecorded path
    /// uses <c>SoundPlayer</c>, which has no volume control of its own. Separate from
    /// <see cref="TtsVolume"/> because these are different sound sources and a user who wants the
    /// callouts quieter than the synthesizer (or vice versa) has no way to say so otherwise.</para></summary>
    [JsonPropertyName("enemyVoiceVolume")]
    public double EnemyVoiceVolume { get; set; } = 1.0;
}

public sealed class GeneralConfig
{
    /// <summary>Documented default: false — never silently auto-launch with the game
    /// until the user opts in.</summary>
    [JsonPropertyName("autoStartWithGame")]
    public bool AutoStartWithGame { get; set; }

    /// <summary>Documented default: "en-US".</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en-US";
}

public sealed class CombosConfig
{
    /// <summary>Saved combo id -> serialized combo JSON string, written by M04
    /// ComboEditor under the dotted keys <c>combos.saved.{id}</c>. A string-valued
    /// dictionary gives those keys a schema home so the typed round-trip on load
    /// preserves them across app restarts instead of dropping them as unknown keys.</summary>
    [JsonPropertyName("saved")]
    public Dictionary<string, string> Saved { get; set; } = new();
}

/// <summary>Combo target-selection preference (user mandate: "대상=같은 포지션 상대+선택 옵션").
/// A schema home for <c>targeting.mode</c> / <c>targeting.manualTarget</c> so the choice survives
/// the typed round-trip on load (unschema'd keys are dropped). Read by <see cref="Combo.ComboRunner"/>
/// at trigger time so a change in the UI takes effect without a restart.</summary>
public sealed class TargetingConfig
{
    /// <summary>"Auto" (default: same-position enemy when the API exposes lanes, else the first
    /// living enemy) or "Manual" (target the champion named by <see cref="ManualTarget"/>).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "Auto";

    /// <summary>The champion name to target when <see cref="Mode"/> is "Manual". Empty in Auto mode.
    /// A dead/absent named target falls through to the Auto rule (never crashes).</summary>
    [JsonPropertyName("manualTarget")]
    public string ManualTarget { get; set; } = string.Empty;
}

/// <summary>Schema home for the M06 Rune Engine's rune-selection UI (rune panel in the M04
/// combo editor). Mirrors <see cref="CombosConfig.Saved"/>'s string-dictionary precedent — a
/// champion id -> serialized <c>Overlay.Core.Runes.RuneSelection</c> JSON string — so the typed
/// round-trip on load preserves <c>runes.selections.{championId}</c> across app restarts instead
/// of dropping it as an unknown key.</summary>
public sealed class RunesConfig
{
    [JsonPropertyName("selections")]
    public Dictionary<string, string> Selections { get; set; } = new();

    /// <summary>User setting: auto-apply the player's genuinely-equipped runes (both the 8 manual
    /// runes and the 6 auto-trigger runes) to combo damage at game start. Default TRUE — the
    /// equipped-rune reflection is the intended default. When FALSE, no rune is auto-applied and only
    /// per-node <c>ComboNode.AttachedRuneId</c> theorycraft overrides contribute. Read live each
    /// trigger by <see cref="Combo.ComboRunner"/> (<c>runes.autoApply</c>), toggled from Settings.</summary>
    [JsonPropertyName("autoApply")]
    public bool AutoApply { get; set; } = true;
}

/// <summary>Schema home for the M04 combo editor's search-to-add "hypothetical build" item
/// picker. Mirrors <see cref="RunesConfig.Selections"/>'s exact precedent — a champion id ->
/// serialized <c>Overlay.Core.Items.ItemBuildSelection</c> JSON string — so
/// <c>items.builds.{championId}</c> survives the typed round-trip across app restarts instead
/// of being dropped as an unknown key.</summary>
public sealed class ItemsConfig
{
    [JsonPropertyName("builds")]
    public Dictionary<string, string> Builds { get; set; } = new();
}

/// <summary>Schema home for <see cref="Combo.TargetSnapshotStore"/> — the "copy target stats"
/// defender theory-crafting feature (loop 38 continuation 19), mirroring <see cref="ItemsConfig.Builds"/>'s
/// exact precedent EXCEPT scoped per COMBO id, not per champion (a combo is tested against one
/// specific hypothetical target). <see cref="Captures"/> holds the captured
/// <c>Overlay.Core.Combo.TargetSnapshot</c> JSON per comboId; <see cref="UseSnapshot"/> is the
/// explicit per-combo opt-in toggle (CLAUDE.md Policy P2: default OFF/absent == false == live
/// resolution unchanged, only true when the user explicitly checks it).</summary>
public sealed class TargetSnapshotsConfig
{
    [JsonPropertyName("captures")]
    public Dictionary<string, string> Captures { get; set; } = new();

    [JsonPropertyName("useSnapshot")]
    public Dictionary<string, bool> UseSnapshot { get; set; } = new();
}
