using System.Text;

namespace Overlay.Core.Combo;

/// <summary>
/// (M40) Keeps one <see cref="ThreatEstimate"/> per enemy, recomputed only when something that
/// enemy's estimate depends on changes: its champion, level, items, keystone or summoner spells, or
/// my max HP / armor / MR by more than a small step. An enemy whose estimate failed (no number) is
/// retried every <see cref="RetryMs"/> rather than waiting for its next level-up — a lookup that
/// was not ready at the first try must not leave a tick missing for minutes. Polled from a
/// background timer; the HUD reads <see cref="Latest"/> every frame without computing anything.
/// </summary>
public sealed class EnemyThreatMonitor
{
    public const long RetryMs = 10_000;

    private readonly Func<GameSnapshot?> _snapshot;
    private readonly Func<GameSnapshot, ScoreboardEntry, ThreatEstimate?> _compute;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, (string Signature, ThreatEstimate? Estimate, long TriedAt)> _cache = new();
    private volatile IReadOnlyList<EnemyThreat> _latest = Array.Empty<EnemyThreat>();

    public EnemyThreatMonitor(Func<GameSnapshot?> snapshot, Func<GameSnapshot, ScoreboardEntry, ThreatEstimate?> compute,
        Func<long>? nowMs = null)
    {
        _snapshot = snapshot;
        _compute = compute;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The last computed estimates, one per enemy with an estimate, in scoreboard order.</summary>
    public IReadOnlyList<EnemyThreat> Latest => _latest;

    /// <summary>Recomputes the enemies whose inputs changed (or whose last try failed long enough
    /// ago). Returns true when anything was recomputed.</summary>
    public bool Tick()
    {
        var snap = _snapshot();
        if (snap is not { HasData: true }) return false;
        var enemies = Enemies(snap);
        if (enemies.Count == 0) return false;

        string mine = MySignature(snap);
        long now = _nowMs();
        bool computed = false;
        var result = new List<EnemyThreat>(enemies.Count);
        foreach (var e in enemies)
        {
            string signature = mine + EnemySignature(e);
            if (!_cache.TryGetValue(e.ChampionName, out var cached) || cached.Signature != signature
                || (cached.Estimate is null && now - cached.TriedAt >= RetryMs))
            {
                cached = (signature, _compute(snap, e), now);
                _cache[e.ChampionName] = cached;
                computed = true;
            }
            if (cached.Estimate is { } estimate) result.Add(new EnemyThreat(e.ChampionName, estimate));
        }
        if (computed) _latest = result;
        return computed;
    }

    private static List<ScoreboardEntry> Enemies(GameSnapshot snap)
    {
        var list = new List<ScoreboardEntry>();
        string? myTeam = null;
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (string.Equals(p.SummonerName, snap.ActivePlayerSummonerName, StringComparison.Ordinal)
                || (p.RiotId.Length > 0 && string.Equals(p.RiotId, snap.ActivePlayerRiotId, StringComparison.Ordinal)))
                myTeam = p.Team;
        }
        if (myTeam is null) return list;
        for (int i = 0; i < snap.PlayerCount; i++)
            if (!string.Equals(snap.Players[i].Team, myTeam, StringComparison.Ordinal))
                list.Add(snap.Players[i]);
        return list;
    }

    /// <summary>My stats, bucketed (25 HP, 2 armor/MR) so a regen tick or a fractional stat never
    /// triggers a recompute.</summary>
    private static string MySignature(GameSnapshot snap)
        => $"{(int)Math.Round(snap.Stats.MaxHealth / 25)}/{(int)Math.Round(snap.Stats.Armor / 2)}/{(int)Math.Round(snap.Stats.MagicResist / 2)}";

    private static string EnemySignature(ScoreboardEntry e)
    {
        var sb = new StringBuilder();
        sb.Append('|').Append(e.Level).Append(':').Append(e.KeystoneId)
          .Append(':').Append(e.Spell1RawName).Append(':').Append(e.Spell2RawName);
        for (int i = 0; i < e.ItemCount && i < e.ItemIds.Length; i++) sb.Append(',').Append(e.ItemIds[i]);
        return sb.ToString();
    }
}

/// <summary>(M40) <see cref="ThreatEstimate"/> keyed by the scoreboard's champion name, which is what
/// the HUD matches against (portraits, dead state).</summary>
public sealed record EnemyThreat(string ScoreboardName, ThreatEstimate Estimate);
