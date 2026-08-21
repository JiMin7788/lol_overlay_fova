using System.Text.Json.Serialization;

namespace Overlay.Core.Lcu;

/// <summary>Parsed League Client lockfile (M33 D1): <c>name:pid:port:password:protocol</c>,
/// written by the client itself next to its executable for exactly this local-API use.</summary>
public sealed record LcuLockfile(int Port, string Password)
{
    /// <summary>Parses the lockfile's single line, or null when the shape is wrong (client
    /// mid-write / truncated read — the caller just retries on its normal cadence).</summary>
    public static LcuLockfile? Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var parts = content.Trim().Split(':');
        if (parts.Length < 5) return null;
        if (!int.TryParse(parts[2], out int port) || port <= 0) return null;
        if (string.IsNullOrEmpty(parts[3])) return null;
        return new LcuLockfile(port, parts[3]);
    }
}

/// <summary>One full rune page in LCU shape (M33 data model): 2 style ids + 6 runes + 3 stat
/// shards in the client's own order — captured verbatim, applied verbatim, never rebuilt.</summary>
public sealed class RunePage
{
    [JsonPropertyName("primaryStyleId")]
    public int PrimaryStyleId { get; set; }

    [JsonPropertyName("subStyleId")]
    public int SubStyleId { get; set; }

    [JsonPropertyName("perkIds")]
    public List<int> PerkIds { get; set; } = new();
}

/// <summary>A saved champ-select preset (M33 D3): the rune page plus optional summoner spells
/// (null spell ids = "don't touch spells on apply", preserving the user's D/F preference when
/// they saved without spells).</summary>
public sealed class RunePreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "기본";

    [JsonPropertyName("championKey")]
    public int ChampionKey { get; set; }

    [JsonPropertyName("page")]
    public RunePage Page { get; set; } = new();

    [JsonPropertyName("spell1Id")]
    public int? Spell1Id { get; set; }

    [JsonPropertyName("spell2Id")]
    public int? Spell2Id { get; set; }

    [JsonPropertyName("savedAt")]
    public string SavedAt { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "local";

    // ── M34 recommendation statistics (null on local presets; the rec rail renders them as
    // 표본/픽률/승률 when present — 2026-07-26 request) ─────────────────────────────────────
    [JsonPropertyName("games")]
    public int? Games { get; set; }

    [JsonPropertyName("winRate")]
    public double? WinRate { get; set; }

    [JsonPropertyName("pickRate")]
    public double? PickRate { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

/// <summary>Own-player champ-select state (M33): numeric champion key (0 = nothing picked or
/// hovered yet) and whether it is locked (picked) vs merely hovered (pick intent).</summary>
public readonly record struct ChampSelectSnapshot(bool InChampSelect, int ChampionKey, bool Locked);

/// <summary>Both teams' champ-select picks and bans (champion keys; 0 = cell not picked yet) —
/// feeds the dashboard composition analysis (2026-07-25).</summary>
public sealed record ChampSelectBoard(
    IReadOnlyList<int> MyTeam, IReadOnlyList<int> TheirTeam,
    IReadOnlyList<int> MyBans, IReadOnlyList<int> TheirBans);

/// <summary>Outcome of a rune-page apply (M33 D2 fencing): the page-slot-exhausted case is
/// surfaced instead of silently overwriting a page the user built by hand.</summary>
public enum ApplyRunesResult
{
    Applied,
    /// <summary>No Fova-managed page exists and no free page slot — the caller must re-invoke
    /// with the explicit overwrite confirmation to replace the CURRENT page.</summary>
    NeedsOverwriteConfirmation,
    Failed,
}
