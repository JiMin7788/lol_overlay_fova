using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Review;

/// <summary>
/// (M42) Records one game for the post-game review, from what is actually observable:
/// <list type="bullet">
/// <item><b>Danger windows</b> — my exact current HP at or below the highest living enemy's
/// estimated full combo (M40 threat line), sampled once a second.</item>
/// <item><b>Deaths</b> — who killed me and whether I was inside (or had just left) a danger
/// window.</item>
/// <item><b>Kills</b> — whom I killed and whether I had checked my combo against them (hotkey) in
/// the <see cref="CheckWindowSeconds"/> before.</item>
/// <item><b>Combo checks</b> — each hotkey result: target and the share of its max HP my combo
/// takes.</item>
/// </list>
/// Enemy current HP is never exposed by the Live Client API, so "how often was the enemy killable"
/// cannot be measured and is not claimed. All data stays on the machine (P1). Thread-safe; never
/// throws.
/// </summary>
public sealed class KillReviewRecorder
{
    /// <summary>A kill counts as "checked" when a combo check on that champion came this many
    /// seconds before it.</summary>
    public const double CheckWindowSeconds = 15;

    /// <summary>A death counts as "in danger" when a danger window was open or closed this many
    /// seconds before it (sampling is once a second, the last hit lands between samples).</summary>
    public const double DangerGraceSeconds = 3;

    /// <summary>Games shorter than this (practice tool pokes, remakes) are not kept.</summary>
    public const double MinGameSeconds = 120;

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;

    private bool _active;
    private DateTime _startedAt;
    private double _lastGameTime;
    private string _champion = string.Empty;
    private int _level, _kills, _deaths, _assists;
    private string _result = string.Empty;
    private readonly List<DangerWindow> _danger = new();
    private DangerWindow? _openDanger;
    private readonly List<ComboCheck> _checks = new();
    private readonly Dictionary<int, (string Killer, string Victim, double Time)> _killEvents = new();

    public KillReviewRecorder(Func<DateTime>? now = null) => _now = now ?? (() => DateTime.Now);

    /// <summary>Feed the latest snapshot and threat estimates (once a second). Returns the finished
    /// review when this sample ends a game (GameEnd seen, the game disappeared, or a new game began),
    /// else null.</summary>
    public GameReview? Sample(GameSnapshot? snap, IReadOnlyList<EnemyThreat> threats)
    {
        lock (_gate)
        {
            try
            {
                if (snap is not { HasData: true })
                    return _active ? FinishLocked() : null;

                GameReview? finished = null;
                if (_active && snap.GameTime + 30 < _lastGameTime) finished = FinishLocked(); // a new game
                // The end screen keeps serving GameEnd for a while: that is the game just saved,
                // not a new one (it used to be saved again every second).
                if (!_active && snap.GameEnded) return finished;
                if (!_active) Begin();
                _lastGameTime = snap.GameTime;

                var me = ResolveMe(snap);
                if (me is not null)
                {
                    _champion = ChampionSummary.ResolveKoreanName(me.ChampionName) ?? me.ChampionName;
                    _level = me.Level;
                    _kills = me.Kills; _deaths = me.Deaths; _assists = me.Assists;
                }

                for (int i = 0; i < snap.KillEventCount; i++)
                {
                    var k = snap.KillEvents[i];
                    if (!_killEvents.ContainsKey(k.EventId))
                        _killEvents[k.EventId] = (Resolve(k.KillerName), Resolve(k.VictimName), k.EventTime);
                }

                SampleDanger(snap, threats);

                if (snap.GameEnded)
                {
                    _result = snap.GameResult;
                    return finished ?? FinishLocked();
                }
                return finished;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>A combo hotkey result: my combo against <paramref name="target"/> takes
    /// <paramref name="killPercent"/> of its max HP.</summary>
    public void OnComboCheck(string target, double killPercent)
    {
        lock (_gate)
        {
            if (!_active || string.IsNullOrEmpty(target)) return;
            // A live refresh re-publishes the same card; one check per target per 5 seconds.
            if (_checks.Count > 0 && _checks[^1].Target == target && _lastGameTime - _checks[^1].Time < 5) return;
            _checks.Add(new ComboCheck(_lastGameTime, target, Math.Round(killPercent, 1)));
        }
    }

    private void Begin()
    {
        _active = true;
        _startedAt = _now();
        _champion = string.Empty;
        _level = _kills = _deaths = _assists = 0;
        _result = string.Empty;
        _danger.Clear();
        _openDanger = null;
        _checks.Clear();
        _killEvents.Clear();
    }

    private void SampleDanger(GameSnapshot snap, IReadOnlyList<EnemyThreat> threats)
    {
        double hp = snap.Stats.CurrentHealth;
        var dead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < snap.PlayerCount; i++)
            if (snap.Players[i].IsDead) dead.Add(snap.Players[i].ChampionName);

        EnemyThreat? top = null;
        foreach (var t in threats)
            if (!dead.Contains(t.ScoreboardName) && (top is null || t.Estimate.Damage > top.Estimate.Damage))
                top = t;

        bool inDanger = hp > 0 && top is not null && hp <= top.Estimate.Damage;
        if (inDanger && _openDanger is null)
            _openDanger = new DangerWindow(snap.GameTime, snap.GameTime, top!.Estimate.ChampionId);
        else if (inDanger && _openDanger is not null)
            _openDanger = _openDanger with { End = snap.GameTime };
        else if (!inDanger && _openDanger is not null)
        {
            _danger.Add(_openDanger with { End = snap.GameTime });
            _openDanger = null;
        }
    }

    private GameReview? FinishLocked()
    {
        _active = false;
        if (_openDanger is not null) { _danger.Add(_openDanger); _openDanger = null; }
        if (_lastGameTime < MinGameSeconds || _champion.Length == 0) return null;

        var deaths = new List<DeathEntry>();
        var kills = new List<KillEntry>();
        foreach (var e in _killEvents.Values.OrderBy(e => e.Time))
        {
            if (e.Victim == MeKey)
                deaths.Add(new DeathEntry(e.Time, e.Killer == MeKey ? "" : e.Killer,
                    _danger.Any(w => e.Time >= w.Start - 0.01 && e.Time <= w.End + DangerGraceSeconds)));
            else if (e.Killer == MeKey)
                kills.Add(new KillEntry(e.Time, e.Victim,
                    _checks.Any(c => c.Target.Equals(e.Victim, StringComparison.OrdinalIgnoreCase)
                                     && c.Time <= e.Time && e.Time - c.Time <= CheckWindowSeconds)));
        }

        return new GameReview(_startedAt, _champion, _level, _kills, _deaths, _assists,
            Math.Round(_lastGameTime), _result, _danger.ToList(), deaths, kills, _checks.ToList());
    }

    // ── name resolution ──────────────────────────────────────────────────────────────
    // Kill events carry player names (or a non-player killer such as a turret); they are resolved
    // to "me" or a champion id when first seen, so the finished review never needs the scoreboard.

    private const string MeKey = "\u0001me";

    private ScoreboardEntry? ResolveMe(GameSnapshot snap)
    {
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            if (SameName(p.SummonerName, snap.ActivePlayerSummonerName) || SameName(p.RiotId, snap.ActivePlayerRiotId)
                || SameName(p.RiotId, snap.ActivePlayerSummonerName) || SameName(p.SummonerName, snap.ActivePlayerRiotId))
            {
                _nameMap = BuildNameMap(snap, p);
                return p;
            }
        }
        return null;
    }

    private Dictionary<string, string> _nameMap = new(StringComparer.OrdinalIgnoreCase);

    private string Resolve(string name) => _nameMap.TryGetValue(name, out var v) ? v : name;

    private static Dictionary<string, string> BuildNameMap(GameSnapshot snap, ScoreboardEntry me)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < snap.PlayerCount; i++)
        {
            var p = snap.Players[i];
            string value = ReferenceEquals(p, me) ? MeKey : ChampionSummary.ResolveKoreanName(p.ChampionName) ?? p.ChampionName;
            foreach (var name in Names(p))
                map[name] = value;
        }
        return map;
    }

    private static IEnumerable<string> Names(ScoreboardEntry p)
    {
        if (p.SummonerName.Length > 0) yield return p.SummonerName;
        if (p.RiotId.Length > 0)
        {
            yield return p.RiotId;
            int hash = p.RiotId.IndexOf('#');
            if (hash > 0) yield return p.RiotId[..hash];
        }
    }

    private static bool SameName(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public sealed record DangerWindow(double Start, double End, string EnemyChampion);
public sealed record DeathEntry(double Time, string KillerChampion, bool InDanger);
public sealed record KillEntry(double Time, string VictimChampion, bool Checked);
public sealed record ComboCheck(double Time, string Target, double KillPercent);

/// <summary>(M42) One finished game. Times are in-game seconds.</summary>
public sealed record GameReview(
    DateTime StartedAt, string Champion, int Level, int Kills, int Deaths, int Assists,
    double LengthSeconds, string Result,
    IReadOnlyList<DangerWindow> DangerWindows, IReadOnlyList<DeathEntry> DeathEntries,
    IReadOnlyList<KillEntry> KillEntries, IReadOnlyList<ComboCheck> ComboChecks)
{
    [JsonIgnore] public int DeathsInDanger => DeathEntries.Count(d => d.InDanger);
    [JsonIgnore] public int CheckedKills => KillEntries.Count(k => k.Checked);
}
