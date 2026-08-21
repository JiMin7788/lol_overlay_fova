using Overlay.Core.ChampionDb;

namespace Overlay.Core.Gold;

/// <summary>
/// M19 §3.3 Global Gold Compare — pure, stateless gold-estimate formula.
///
/// <para><b>Why an estimate at all:</b> the Live Client Data API exposes <c>currentGold</c>
/// only for the ACTIVE (local) player (see <see cref="LiveClientEventPublisher"/>'s own gold
/// comment — publishing it for anyone else would be "beyond what the API/screen provides",
/// P1). A team-wide gold
/// comparison must therefore be ESTIMATED from public per-player scoreboard fields M01 already
/// parses (creepScore, kills, assists, gameTime) — never presented as the real server ledger
/// (P2). Every constant below is cited; this class computes nothing it cannot source.</para>
///
/// <para><b>Formula and sourcing:</b>
/// <list type="bullet">
/// <item><description><see cref="StartingGold"/> = 500 — the standard Summoner's Rift starting
/// gold (all current game modes this app targets).</description></item>
/// <item><description><see cref="PassiveGoldPerSecond"/> = 2.004 g/s — League's long-published
/// flat passive gold generation rate (~120.24 gold/min), public game-design knowledge
/// (Riot dev communications / community wiki "Gold (League of Legends)"). This class applies
/// it as a SIMPLIFIED flat rate starting at <see cref="PassiveGoldRampEndSeconds"/> (~1:50),
/// deliberately not modeling the exact partial-rate ramp between 1:10 and 1:50 (would need
/// per-tick integration this snapshot-based formula does not have) — this slightly
/// UNDER-counts very early gold, a bounded and disclosed approximation.</description></item>
/// <item><description><see cref="BlendedCsGoldPerCs"/> = 20.5 g/CS — the Live Client API's
/// <c>creepScore</c> is a single already-summed int with NO minion-type breakdown (caster ~14g,
/// melee ~21g, siege/canon more, jungle camp CS worth more), so this is a single BLENDED average
/// across common lane + jungle CS, not Riot's exact per-minion value. Documented approximation,
/// not exact.</description></item>
/// <item><description><see cref="KillBountyGold"/> = 300 / <see cref="AssistBountyGold"/> = 150 —
/// public base kill/assist bounty values at even gold. This class does NOT model kill streaks,
/// shutdown bounties, or comeback mechanics (the API exposes no gold-differential history to
/// derive those from) — flat base values only.</description></item>
/// </list></para>
///
/// <para>The composed result is ALWAYS estimate-flagged (<see cref="TeamGoldEstimate.IsEstimate"/>
/// is always true) and the HUD renders it with an explicit "(추정)" label — never as confirmed
/// data (M19 §3.3 / Hard Rule P2).</para>
///
/// <para><b>v2 (M07 "Pending User-Reported Changes" — item-driven recompute):</b> a player's gold
/// actually spent on their visible items (<see cref="ItemRepository"/>, M11, sourced from Data
/// Dragon) is a more direct, already-available signal than continuously modeling time-based
/// passive accrual, so <see cref="EstimatePlayerGold"/> now also takes that player's current item
/// ids and computes <c>StartingGold + sum(item.GoldTotal)</c> as an ITEMS estimate. The original
/// starting-gold/passive/CS/kill-bounty formula (the EARNED estimate) is kept as a supplementary
/// signal for gold accumulated but not yet spent BETWEEN purchases (per the spec's own wording),
/// since it keeps growing every tick while the items estimate is flat between item events. The two
/// are combined with <c>Math.Max</c> rather than summed — both are independent estimates of the
/// same "total gold this player has had access to" quantity (items-spent is a subset of
/// earned-total), so summing them would double count; taking the larger of the two lets whichever
/// signal is currently more informative dominate without double-counting. Still always
/// estimate-flagged (P2 unchanged).</para>
/// </summary>
public static class GoldEstimate
{
    public const double StartingGold = 500.0;
    public const double PassiveGoldPerSecond = 2.004;
    public const double PassiveGoldRampEndSeconds = 110.0; // ~1:50, end of the real partial ramp
    public const double BlendedCsGoldPerCs = 20.5;
    public const double KillBountyGold = 300.0;
    public const double AssistBountyGold = 150.0;

    /// <summary>One player's estimated total gold. Primary signal: <paramref name="itemIds"/>
    /// (first <paramref name="itemCount"/> entries) priced via <see cref="ItemRepository"/> —
    /// <c>StartingGold + sum(item.GoldTotal)</c>, the ITEMS estimate. Supplementary signal: the
    /// original starting-gold + passive-over-time + CS + kill/assist-bounty formula (the EARNED
    /// estimate), which fills the gap BETWEEN item purchases (see class doc "v2"). The two are
    /// combined with <c>Math.Max</c> to avoid double-counting. <paramref name="itemIds"/> defaults
    /// to null/0 (no items known) so existing callers that only pass the scoreboard fields keep
    /// getting exactly the EARNED estimate, unchanged. Pure function — directly unit-testable
    /// against hand-computed values.</summary>
    public static double EstimatePlayerGold(
        int creepScore, int kills, int assists, double gameTimeSeconds,
        int[]? itemIds = null, int itemCount = 0)
    {
        double passive = PassiveGoldPerSecond * Math.Max(0.0, gameTimeSeconds - PassiveGoldRampEndSeconds);
        double cs = creepScore * BlendedCsGoldPerCs;
        double combat = kills * KillBountyGold + assists * AssistBountyGold;
        double earnedEstimate = StartingGold + passive + cs + combat;

        double itemsEstimate = EstimateItemsGold(itemIds, itemCount);

        return Math.Max(earnedEstimate, itemsEstimate);
    }

    /// <summary><c>StartingGold + sum(item.GoldTotal)</c> for the first <paramref name="itemCount"/>
    /// ids in <paramref name="itemIds"/>. Unknown item ids (not found in <see cref="ItemRepository"/>)
    /// are skipped rather than throwing — mirrors M07 <c>ItemTracker</c>'s own tolerant lookup
    /// pattern, since a not-yet-loaded/unrecognized id must not crash the whole team estimate.</summary>
    private static double EstimateItemsGold(int[]? itemIds, int itemCount)
    {
        double sum = StartingGold;
        if (itemIds is null) return sum;

        for (int i = 0; i < itemCount; i++)
        {
            var item = ItemRepository.Get(itemIds[i].ToString());
            if (item is not null) sum += item.GoldTotal;
        }
        return sum;
    }

    /// <summary>Sums <see cref="EstimatePlayerGold"/> across the scoreboard, split by team
    /// relative to the active player (their team = "ally"). Returns false if the active
    /// player's own scoreboard row cannot be resolved (team unknown; e.g. not yet synced) or
    /// there is no scoreboard data at all.</summary>
    public static bool TryCompute(GameSnapshot snapshot, out TeamGoldEstimate result)
    {
        result = default;
        if (snapshot is null || !snapshot.HasData || snapshot.PlayerCount == 0) return false;

        string activeTeam = FindActiveTeam(snapshot);
        if (activeTeam.Length == 0) return false;

        double ally = 0, enemy = 0;
        for (int i = 0; i < snapshot.PlayerCount; i++)
        {
            ScoreboardEntry p = snapshot.Players[i];
            if (p.ChampionName.Length == 0) continue; // unpopulated slot

            double g = EstimatePlayerGold(p.CreepScore, p.Kills, p.Assists, snapshot.GameTime, p.ItemIds, p.ItemCount);
            if (string.Equals(p.Team, activeTeam, StringComparison.Ordinal))
                ally += g;
            else
                enemy += g;
        }

        result = new TeamGoldEstimate(ally, enemy, ally - enemy, IsEstimate: true);
        return true;
    }

    /// <summary>Resolves the active player's team by matching <see cref="GameSnapshot.ActivePlayerRiotId"/>
    /// against scoreboard rows (the reliable id — see <see cref="ScoreboardEntry.RiotId"/>'s doc),
    /// falling back to the legacy SummonerName match. Empty string if neither resolves.</summary>
    private static string FindActiveTeam(GameSnapshot snapshot)
    {
        if (snapshot.ActivePlayerRiotId.Length > 0)
        {
            for (int i = 0; i < snapshot.PlayerCount; i++)
            {
                ScoreboardEntry p = snapshot.Players[i];
                if (p.RiotId.Length > 0 && string.Equals(p.RiotId, snapshot.ActivePlayerRiotId, StringComparison.Ordinal))
                    return p.Team;
            }
        }

        if (snapshot.ActivePlayerSummonerName.Length > 0)
        {
            for (int i = 0; i < snapshot.PlayerCount; i++)
            {
                ScoreboardEntry p = snapshot.Players[i];
                if (p.SummonerName.Length > 0 && string.Equals(p.SummonerName, snapshot.ActivePlayerSummonerName, StringComparison.Ordinal))
                    return p.Team;
            }
        }

        return string.Empty;
    }
}

/// <summary>Result of <see cref="GoldEstimate.TryCompute"/>: estimated team gold totals and
/// their difference (ally - enemy). <see cref="IsEstimate"/> is always true — kept as an
/// explicit field (rather than an implicit convention) so a future consumer cannot forget the
/// P2 estimate label.</summary>
public readonly record struct TeamGoldEstimate(double AllyGold, double EnemyGold, double Diff, bool IsEstimate);
