using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.ChampSelect;

/// <summary>
/// Champ-select team composition summary (2026-07-25 request): physical/magic lean from Riot's
/// own <c>info.attack</c>/<c>info.magic</c> scores (<see cref="ChampionInfoDb"/>, P1 official
/// data) plus a true-damage flag derived from this project's curated skill data — a champion
/// counts as carrying true damage when any curated hit (direct or bonus-effect) is typed True.
/// </summary>
public static class TeamCompAnalyzer
{
    private static readonly string[] Slots = { "P", "Q", "W", "E", "R" };

    public sealed record Row(int Key, string Id, string Name, int Attack, int Magic, bool HasTrueDamage);

    /// <summary>AdShare/ApShare are Riot's attack/magic score fractions (sum to 1 when any data
    /// exists) — used for the comp HINT. Phys/Magic/TrueShare are the three-way damage-TYPE
    /// tendency (sum to 1) behind the stacked bar: derived from this project's curated skill
    /// hits (count-weighted per type; fallback to the Riot scores for an uncurated champion), so
    /// true damage gets a real, data-backed slice instead of a fabricated weight. A tendency,
    /// not a damage forecast — magnitudes depend on builds and game state.</summary>
    public sealed record Comp(IReadOnlyList<Row> Rows, double AdShare, double ApShare, int TrueCount,
        double PhysShare, double MagicShare, double TrueShare);

    public static Comp Analyze(IEnumerable<int> championKeys)
    {
        var rows = new List<Row>();
        int attack = 0, magic = 0, trueCount = 0;
        double phys = 0, mag = 0, tru = 0;
        foreach (int key in championKeys)
        {
            if (key <= 0 || ChampionInfoDb.GetByKey(key) is not { } info) continue;
            var (p, m, t) = CuratedTypeWeights(info.Id);
            if (p + m + t <= 0) { p = info.Attack; m = info.Magic; t = 0; } // uncurated fallback
            double sum = p + m + t;
            if (sum > 0) { phys += p / sum; mag += m / sum; tru += t / sum; } // each champ weighs 1
            bool hasTrue = t > 0;
            rows.Add(new Row(key, info.Id, info.Name, info.Attack, info.Magic, hasTrue));
            attack += info.Attack;
            magic += info.Magic;
            if (hasTrue) trueCount++;
        }
        int total = attack + magic;
        double typeTotal = phys + mag + tru;
        return new Comp(rows,
            total > 0 ? (double)attack / total : 0,
            total > 0 ? (double)magic / total : 0,
            trueCount,
            typeTotal > 0 ? phys / typeTotal : 0,
            typeTotal > 0 ? mag / typeTotal : 0,
            typeTotal > 0 ? tru / typeTotal : 0);
    }

    /// <summary>Count-weighted damage-type totals over every curated hit (direct + bonus-effect)
    /// of a champion; (0,0,0) when uncurated.</summary>
    private static (double Phys, double Magic, double True) CuratedTypeWeights(string championId)
    {
        double p = 0, m = 0, t = 0;
        void Add(SkillHit hit)
        {
            double w = Math.Max(1, hit.Count);
            if (hit.Type == HitDamageType.Physical) p += w;
            else if (hit.Type == HitDamageType.Magic) m += w;
            else if (hit.Type == HitDamageType.True) t += w;
        }
        try
        {
            foreach (var slot in Slots)
            {
                foreach (var hit in SkillDamageDb.GetHits(championId, slot) ?? Array.Empty<SkillHit>())
                    Add(hit);
                foreach (var eff in SkillDamageDb.GetBonusEffects(championId, slot) ?? Array.Empty<SkillBonusEffect>())
                    foreach (var hit in eff.Hits)
                        Add(hit);
            }
        }
        catch { /* uncurated/missing champion → zeros, caller falls back */ }
        return (p, m, t);
    }
}
