using System.Text.Json;
using Overlay.Core.Config;
using Overlay.Core.Lcu;

namespace Overlay.Core.ChampSelect;

/// <summary>M33 D4 — a source of presets for one champion. MVP has only
/// <see cref="LocalPresetSource"/>; phase 2 adds a remote recommendation source behind this same
/// interface (the panel and apply path never know a preset's origin).</summary>
public interface IRunePresetSource
{
    IReadOnlyList<RunePreset> List(int championKey);
}

/// <summary>
/// M33 D3 — config-backed per-champion preset store. Follows the
/// <c>combos.saved</c>/<c>runes.selections</c> string-dictionary precedent exactly: one JSON
/// string (the champion's preset ARRAY) per numeric champion key under
/// <c>champSelect.presets.{championKey}</c>, whose schema home is
/// <see cref="ChampSelectConfig.Presets"/> so the typed round-trip preserves it.
/// Distinct from <see cref="Runes.RuneSelectionStore"/> on purpose: that store feeds the M06
/// combo-damage rune engine (string rune ids, manual flags); this one stores full LCU page
/// shapes for the champ-select applier.
/// </summary>
public sealed class ChampSelectPresets : IRunePresetSource
{
    private const string KeyPrefix = "champSelect.presets.";
    private static readonly JsonSerializerOptions Options = new();

    private readonly ConfigManager _config;

    public ChampSelectPresets(ConfigManager config) => _config = config;

    /// <summary>All saved presets for the champion, oldest first. Empty (never null) when none
    /// or corrupt — a corrupt entry reads as "nothing saved", never a crash.</summary>
    public IReadOnlyList<RunePreset> List(int championKey)
    {
        if (_config.Get(KeyPrefix + championKey) is not string raw) return Array.Empty<RunePreset>();
        try { return JsonSerializer.Deserialize<List<RunePreset>>(raw, Options) ?? new(); }
        catch (JsonException) { return Array.Empty<RunePreset>(); }
    }

    /// <summary>Adds or replaces (by <see cref="RunePreset.Name"/>) one preset.</summary>
    public void Save(RunePreset preset)
    {
        var list = new List<RunePreset>(List(preset.ChampionKey));
        int existing = list.FindIndex(p => p.Name == preset.Name);
        if (existing >= 0) list[existing] = preset;
        else list.Add(preset);
        _config.Set(KeyPrefix + preset.ChampionKey, JsonSerializer.Serialize(list, Options));
    }

    public void Delete(int championKey, string name)
    {
        var list = new List<RunePreset>(List(championKey));
        if (list.RemoveAll(p => p.Name == name) > 0)
            _config.Set(KeyPrefix + championKey, JsonSerializer.Serialize(list, Options));
    }
}

/// <summary>
/// M33 auto-apply gate — the P4-critical rule set, kept pure for unit tests: fires at most ONCE
/// per champ-select session, only on a LOCKED champion, only when the standing opt-in
/// (<c>champSelect.autoApply</c>, default false) is on, and only for LOCAL presets (phase-2
/// recommendation sources never auto-apply). The caller (client panel) performs the actual
/// apply when this returns a preset.
/// </summary>
public sealed class AutoApplyGate
{
    private bool _firedThisSession;

    /// <summary>Feed every snapshot change; returns the preset to auto-apply now, or null.</summary>
    public RunePreset? OnSnapshot(ChampSelectSnapshot snap, bool optedIn, IRunePresetSource localPresets)
    {
        if (!snap.InChampSelect)
        {
            _firedThisSession = false; // session over -> re-arm for the next champ select
            return null;
        }
        if (!optedIn || _firedThisSession || !snap.Locked || snap.ChampionKey <= 0) return null;

        var presets = localPresets.List(snap.ChampionKey);
        foreach (var p in presets)
        {
            if (p.Source == "local")
            {
                _firedThisSession = true;
                return p;
            }
        }
        // A champion with no local preset does not consume the once-per-session shot: the user
        // may still swap to a champion that HAS one (rare, but costless to allow).
        return null;
    }
}
