namespace Overlay.Core;

/// <summary>
/// Reusable, pre-allocated container for a single tick's parsed game state.
/// The poller keeps two of these (current + previous) and swaps between them,
/// so steady-state polling does not allocate on the heap (Hard Rule #2).
///
/// Fields are intentionally flat/struct-like. Strings are the only managed
/// allocation; the parser reuses backing buffers and only materializes strings
/// for identity-bearing values (champion/player/item names) that downstream
/// consumers need. Reset() clears counts without releasing the backing arrays.
/// </summary>
public sealed class GameSnapshot
{
    public const int MaxPlayers = 10;
    public const int MaxItemsPerPlayer = 8; // 6 item slots + trinket + 2026 role-quest slot (loop 135)

    // ---- gameData ----
    public double GameTime;
    public bool HasData; // false => no active game / empty payload

    // ---- activePlayer ----
    public string ActivePlayerSummonerName = string.Empty;
    /// <summary>activePlayer.riotId ("GameName#TAG"). Modern Live Client patches populate
    /// this consistently for BOTH the active player and every scoreboard row, whereas
    /// summonerName is deprecated and often differs in format between the two (e.g. active =
    /// "Name#KR1", scoreboard = ""), which broke active-player matching. Kept alongside
    /// summonerName so matching can prefer the reliable id and fall back to the legacy one.</summary>
    public string ActivePlayerRiotId = string.Empty;
    public double CurrentGold;
    public int Level;
    public ActivePlayerStats Stats;

    // ---- allPlayers (scoreboard) ----
    public int PlayerCount;
    public readonly ScoreboardEntry[] Players;

    // ---- events ----
    /// <summary>Highest EventID seen this tick. The Live Client events list is
    /// append-only with monotonically increasing IDs, so the count/max is enough
    /// to detect newly added events without storing the whole list.</summary>
    public int MaxEventId;
    public int EventCount;

    /// <summary>Generous cap on tracked ChampionKill events per match (M01 gap #5:
    /// killerName sourcing). The Live Client <c>events</c> payload resends the full,
    /// ever-growing history every tick; beyond this cap additional kills are skipped
    /// defensively (same "no crash, just stop tracking new instances" pattern as
    /// <see cref="MaxPlayers"/>) rather than reallocating/crashing.</summary>
    public const int MaxKillEvents = 128;

    /// <summary>Number of ChampionKill events captured into <see cref="KillEvents"/>
    /// this tick (out of the full, cumulative Events list).</summary>
    public int KillEventCount;

    /// <summary>ChampionKill events parsed from the Events array, in ascending
    /// EventID order. Slot <c>i</c> holds the same historical kill on every tick
    /// (kills are append-only and never reordered), which lets the parser reuse each
    /// slot's Killer/VictimName strings for a <c>ValueTextEquals</c> byte-compare and
    /// avoid reallocating for the many already-seen kills re-sent every 0.1s tick.</summary>
    public readonly KillEventRecord[] KillEvents;

    /// <summary>Cap on tracked InhibKilled events per match (M19 §3.2 M01 parser
    /// extension). A match has at most 6 inhibitors, so this is generous headroom.</summary>
    public const int MaxInhibEvents = 16;

    /// <summary>Number of InhibKilled events captured into <see cref="InhibEvents"/>
    /// this tick, mirroring <see cref="KillEventCount"/>.</summary>
    public int InhibEventCount;

    /// <summary>InhibKilled events parsed from the Events array, in ascending EventID
    /// order — same append-only/never-reordered slot semantics as <see cref="KillEvents"/>.</summary>
    public readonly InhibEventRecord[] InhibEvents;

    /// <summary>Cap on tracked TurretKilled events per match (patch 15.1 Nexus-turret
    /// respawn timer, M01 parser extension). A match has at most ~24 turrets; 32 gives
    /// generous headroom. Only the two Nexus turrets per base drive a respawn timer, but
    /// the raw parser stays generic (M01 = raw data layer) and the domain layer filters.</summary>
    public const int MaxTurretEvents = 32;

    /// <summary>Number of TurretKilled events captured into <see cref="TurretEvents"/>
    /// this tick, mirroring <see cref="InhibEventCount"/>.</summary>
    public int TurretEventCount;

    /// <summary>TurretKilled events parsed from the Events array, ascending EventID order —
    /// same append-only/never-reordered slot semantics as <see cref="InhibEvents"/>.</summary>
    public readonly TurretEventRecord[] TurretEvents;

    public GameSnapshot()
    {
        Players = new ScoreboardEntry[MaxPlayers];
        for (int i = 0; i < Players.Length; i++)
            Players[i] = new ScoreboardEntry();

        KillEvents = new KillEventRecord[MaxKillEvents];
        for (int i = 0; i < KillEvents.Length; i++)
            KillEvents[i] = new KillEventRecord();

        InhibEvents = new InhibEventRecord[MaxInhibEvents];
        for (int i = 0; i < InhibEvents.Length; i++)
            InhibEvents[i] = new InhibEventRecord();

        TurretEvents = new TurretEventRecord[MaxTurretEvents];
        for (int i = 0; i < TurretEvents.Length; i++)
            TurretEvents[i] = new TurretEventRecord();
    }

    /// <summary>Full reset — clears identity strings too. Used to establish a
    /// disconnect baseline, not on the hot parse path.</summary>
    public void Reset()
    {
        ActivePlayerSummonerName = string.Empty;
        ActivePlayerRiotId = string.Empty;
        for (int i = 0; i < Players.Length; i++)
            Players[i].Reset();
        for (int i = 0; i < KillEvents.Length; i++)
            KillEvents[i].Reset();
        for (int i = 0; i < InhibEvents.Length; i++)
            InhibEvents[i].Reset();
        for (int i = 0; i < TurretEvents.Length; i++)
            TurretEvents[i].Reset();
        ResetForParse();
    }

    /// <summary>Reset counts/flags before parsing a new tick while KEEPING identity
    /// strings (active-player + scoreboard names) so the parser can byte-compare
    /// and skip allocating unchanged strings (Hard Rule #2). Does not free arrays.</summary>
    public void ResetForParse()
    {
        GameTime = 0;
        HasData = false;
        CurrentGold = 0;
        Level = 0;
        Stats = default;
        // EquippedRuneIds must never be null (LiveDataParser only assigns it when the payload
        // carries a fullRunes block; older API shapes / spectator mode omit it entirely). Reset
        // to the shared empty-array singleton here so a champ-select/spectator tick without
        // fullRunes still leaves callers a safe, non-null, empty list — same "no fabricated
        // data" contract as every other field on this snapshot.
        Stats.EquippedRuneIds = Array.Empty<int>();
        PlayerCount = 0;
        MaxEventId = -1;
        EventCount = 0;
        // KillEvents entries are NOT reset here: their KillerName/VictimName strings
        // are kept across ticks (same slot == same historical kill every tick, see
        // KillEvents doc) so the parser can byte-compare instead of reallocating for
        // the many already-seen kills the API resends every tick. Only the count
        // (how many slots this tick actually populated) resets.
        KillEventCount = 0;
        // InhibEvents entries are likewise NOT reset here — same append-only slot reuse
        // as KillEvents (see above). Only the per-tick count resets.
        InhibEventCount = 0;
        // TurretEvents: same append-only slot-reuse contract as InhibEvents.
        TurretEventCount = 0;
        for (int i = 0; i < Players.Length; i++)
            Players[i].ResetForParse();
    }

    /// <summary>Deep-copy this snapshot's contents into <paramref name="dest"/>,
    /// reusing dest's existing arrays/entries (no allocation beyond interned-ish
    /// string references, which are shared not copied).</summary>
    public void CopyTo(GameSnapshot dest)
    {
        dest.GameTime = GameTime;
        dest.HasData = HasData;
        dest.ActivePlayerSummonerName = ActivePlayerSummonerName;
        dest.ActivePlayerRiotId = ActivePlayerRiotId;
        dest.CurrentGold = CurrentGold;
        dest.Level = Level;
        dest.Stats = Stats;
        dest.MaxEventId = MaxEventId;
        dest.EventCount = EventCount;
        dest.PlayerCount = PlayerCount;
        for (int i = 0; i < PlayerCount; i++)
            Players[i].CopyTo(dest.Players[i]);
        for (int i = PlayerCount; i < dest.Players.Length; i++)
            dest.Players[i].Reset();

        dest.KillEventCount = KillEventCount;
        for (int i = 0; i < KillEventCount; i++)
            KillEvents[i].CopyTo(dest.KillEvents[i]);

        dest.InhibEventCount = InhibEventCount;
        for (int i = 0; i < InhibEventCount; i++)
            InhibEvents[i].CopyTo(dest.InhibEvents[i]);

        dest.TurretEventCount = TurretEventCount;
        for (int i = 0; i < TurretEventCount; i++)
            TurretEvents[i].CopyTo(dest.TurretEvents[i]);
    }
}

/// <summary>Subset of activePlayer.championStats relevant to downstream features.
/// Pure value type — no allocation.</summary>
public struct ActivePlayerStats
{
    public double CurrentHealth;
    public double MaxHealth;
    public double ResourceValue;
    public double ResourceMax;
    public double AttackDamage;
    public double AbilityPower;
    public double Armor;
    public double MagicResist;
    public double MoveSpeed;

    /// <summary>Final attacks per second (activePlayer.championStats.attackSpeed, P1 public own-stat).
    /// 0 when not reported. Backs the BIN mStat=4 mapping in SkillDamage (§48): TOTAL = this value,
    /// BONUS = the RATIO total/base − 1 (attack-speed formulas consume bonus AS as a fraction, e.g.
    /// Akshan E's ×(1 + 0.3 × bonusAS) — level growth counts as bonus, so deriving from live total
    /// over the champion's flat base captures items + levels together).</summary>
    public double AttackSpeed;

    // Active player's own penetration stats from activePlayer.championStats (P1: the
    // player's public in-game stats). Percent fields hold OUR additive-fraction convention
    // (0.30 = 30% bonus pen, baseline/no items = 0.0) — LiveDataParser converts Riot's raw API
    // value (a remaining-resistance MULTIPLIER, baseline = 1.0) via `1.0 - raw` at parse time
    // (loop-38 fix, confirmed live: reading the raw multiplier directly made a naked attacker's
    // percent-pen always compute as 1.0, zeroing EffectiveResistMultiplier's resistance term for
    // every hit regardless of the target's real armor/MR). Carried by the existing
    // `dest.Stats = Stats` value-copy and cleared by `Stats = default`.
    public double ArmorPenetrationFlat;
    public double ArmorPenetrationPercent;
    public double MagicPenetrationFlat;
    public double MagicPenetrationPercent;

    // Active player's real crit stats from activePlayer.championStats (P1: the player's
    // public in-game stats), confirmed against Riot's public Live Client Data API sample
    // JSON (static.developer.riotgames.com/docs/lol/liveclientdata_sample.json), which
    // lists both fields verbatim under championStats. CritChance is a 0-1 fraction (0.25
    // = 25% crit chance), same convention DamageEngine.AttackerStat.CriticalChance already
    // expects — no unit conversion needed at the parse boundary (unlike the percent-pen
    // fields above). CritDamage's exact real-game convention (total multiplier, e.g. 2.0 for
    // 200%, vs. some other unit) is NOT independently confirmed live in this sandbox (no
    // dotnet SDK / no live game to sample); DamageEngine treats 0 as "not reported" and
    // falls back to its own documented 1.75 constant rather than trusting an unverified
    // interpretation of a nonzero-but-unconfirmed value's units. UNVERIFIED by compiler.
    public double CritChance;
    public double CritDamage;

    // Active player's REAL ability ranks (0-5, R is 0-3) from activePlayer.abilities.
    // 0 means the slot is not leveled yet (or the abilities block was absent). Combo
    // damage uses these so it reflects the player's actual in-game skill levels instead
    // of always assuming max rank. As a value type these are carried automatically by the
    // existing `dest.Stats = Stats` copy and cleared by `Stats = default` (see GameSnapshot).
    public int AbilityQ;
    public int AbilityW;
    public int AbilityE;
    public int AbilityR;

    /// <summary>Every rune id the active player has REALLY equipped this game — the primary
    /// tree's keystone plus every other non-stat-shard rune, from activePlayer.fullRunes
    /// (keystone.id + generalRunes[].id; statRunes intentionally excluded, they aren't runes).
    /// Same numeric id scheme as Data Dragon's runesReforged.json / <see cref="ChampionDb.RuneData.Id"/>
    /// (confirmed via <see cref="ChampionDb.DDragonParser.ParseRunes"/>, which keys RuneData.Id off
    /// the identical raw perk id). Empty (never null — see <see cref="GameSnapshot.ResetForParse"/>)
    /// when fullRunes is absent from the payload (spectator mode / older API shape) — P2: no
    /// inference, no fabricated selection. Consumed by
    /// <see cref="Combo.ComboRunner"/>'s auto rune-load, replacing the manual picker
    /// (<see cref="Runes.RuneSelectionStore"/>) as the primary source whenever live data is
    /// available.</summary>
    public IReadOnlyList<int> EquippedRuneIds;
}

/// <summary>One scoreboard row. A class (not struct) so it can live in a reusable
/// pooled array and be mutated in place across ticks.</summary>
public sealed class ScoreboardEntry
{
    public string SummonerName = string.Empty;
    /// <summary>allPlayers[].riotId ("GameName#TAG") — the reliable cross-row identity used to
    /// match this row against the active player (see <see cref="GameSnapshot.ActivePlayerRiotId"/>).</summary>
    public string RiotId = string.Empty;
    public string ChampionName = string.Empty;
    public string Team = string.Empty; // "ORDER" / "CHAOS"
    /// <summary>allPlayers[].position ("TOP"/"JUNGLE"/"MIDDLE"/"BOTTOM"/"UTILITY"). BEST-EFFORT:
    /// the Live Client API populates this only in ranked/draft and leaves it "" in practice tool,
    /// ARAM, etc. — so same-position auto-targeting (see <see cref="Combo.ComboRunner"/>) uses it
    /// only when present and otherwise falls back. Treated as an identity string (stable per slot
    /// within a match) so it is kept across a per-tick reset for the byte-compare fast path.</summary>
    public string Position = string.Empty;
    /// <summary>allPlayers[].summonerSpells.summonerSpellOne/Two.rawDisplayName (e.g.
    /// "SummonerSmite", "SummonerFlash") — raw internal spell name, present in every game mode
    /// (unlike <see cref="Position"/>). Added for M31 P3's Smite-based jungler-identification
    /// fallback (docs/modules/M31_MINIMAP_VISION.md §9 decision 1); a raw passthrough string like
    /// the other identity fields on this row, not a domain-interpreted flag — the caller decides
    /// what a given raw name means (M31's <c>EnemyJunglerIdentifier</c> is the first consumer).</summary>
    public string Spell1RawName = string.Empty;
    public string Spell2RawName = string.Empty;
    public int Level;
    public bool IsDead;
    public double RespawnTimer;
    public int Kills;
    public int Deaths;
    public int Assists;
    public int CreepScore;

    public int ItemCount;
    public readonly int[] ItemIds = new int[GameSnapshot.MaxItemsPerPlayer];
    /// <summary>allPlayers[].items[].count, parallel to <see cref="ItemIds"/> — stack quantity for
    /// stackable consumables (Health Potion, wards, etc.), 1 for non-stackable equipment. Added so a
    /// consumable being USED (same itemId/slot, count decreases — e.g. 2 potions -> 1) is a real,
    /// detectable field-level change, distinct from an item disappearing entirely.</summary>
    public readonly int[] ItemCounts = new int[GameSnapshot.MaxItemsPerPlayer];

    public void Reset()
    {
        SummonerName = string.Empty;
        RiotId = string.Empty;
        ChampionName = string.Empty;
        Team = string.Empty;
        Position = string.Empty;
        Spell1RawName = string.Empty;
        Spell2RawName = string.Empty;
        ResetForParse();
    }

    /// <summary>Clear per-tick numeric/item state but KEEP the identity strings
    /// (SummonerName/ChampionName/Team) so the parser can byte-compare incoming
    /// values against them and skip allocating an unchanged string (Hard Rule #2).
    /// The API returns players in stable slot order within a match, so reusing the
    /// slot's prior identity for comparison is valid.</summary>
    public void ResetForParse()
    {
        Level = 0;
        IsDead = false;
        RespawnTimer = 0;
        Kills = Deaths = Assists = CreepScore = 0;
        ItemCount = 0;
        Array.Clear(ItemIds);
        Array.Clear(ItemCounts);
    }

    public void CopyTo(ScoreboardEntry dest)
    {
        dest.SummonerName = SummonerName;
        dest.RiotId = RiotId;
        dest.ChampionName = ChampionName;
        dest.Team = Team;
        dest.Position = Position;
        dest.Spell1RawName = Spell1RawName;
        dest.Spell2RawName = Spell2RawName;
        dest.Level = Level;
        dest.IsDead = IsDead;
        dest.RespawnTimer = RespawnTimer;
        dest.Kills = Kills;
        dest.Deaths = Deaths;
        dest.Assists = Assists;
        dest.CreepScore = CreepScore;
        dest.ItemCount = ItemCount;
        Array.Copy(ItemIds, dest.ItemIds, ItemIds.Length);
        Array.Copy(ItemCounts, dest.ItemCounts, ItemCounts.Length);
    }

    /// <param name="count">Stack quantity (allPlayers[].items[].count); defaults to 1 for the common
    /// non-stackable case so existing call sites that don't track quantity keep compiling unchanged.</param>
    public bool TryAddItem(int itemId, int count = 1)
    {
        if (ItemCount >= ItemIds.Length) return false;
        ItemIds[ItemCount] = itemId;
        ItemCounts[ItemCount] = count;
        ItemCount++;
        return true;
    }
}

/// <summary>One ChampionKill entry parsed from the Live Client <c>events</c> payload
/// (M01 gap #5). <see cref="KillerName"/>/<see cref="VictimName"/> are the raw
/// identity strings the API reports for that event (same format as
/// <see cref="ScoreboardEntry.SummonerName"/>) — resolving them to a championName is
/// the caller's job (via the scoreboard), since the events feed itself never carries
/// championName. A class (not struct), like <see cref="ScoreboardEntry"/>, so a stable
/// slot can be mutated/byte-compared in place across ticks instead of reallocated.</summary>
public sealed class KillEventRecord
{
    public int EventId;
    public bool IsChampionKill;
    public string KillerName = string.Empty;
    public string VictimName = string.Empty;
    public double EventTime;

    public void Reset()
    {
        KillerName = string.Empty;
        VictimName = string.Empty;
        ResetForParse();
    }

    /// <summary>Clears the cheap value-type fields but KEEPS KillerName/VictimName so
    /// the parser can byte-compare against them (Hard Rule #2) — see
    /// <see cref="GameSnapshot.KillEvents"/>.</summary>
    public void ResetForParse()
    {
        EventId = 0;
        IsChampionKill = false;
        EventTime = 0;
    }

    public void CopyTo(KillEventRecord dest)
    {
        dest.EventId = EventId;
        dest.IsChampionKill = IsChampionKill;
        dest.KillerName = KillerName;
        dest.VictimName = VictimName;
        dest.EventTime = EventTime;
    }
}

/// <summary>One InhibKilled entry parsed from the Live Client <c>events</c> payload
/// (M19 §3.2 M01 parser extension). <see cref="InhibId"/> is the raw Riot identifier
/// carried on the event's own <c>InhibKilled</c> field (e.g. <c>"Barracks_T1_L1"</c>) —
/// it already encodes which team's inhibitor and which lane, so it is kept as-is rather
/// than decoded into separate Team/Lane fields (the exact T#/L#/C#/R# encoding is not
/// confirmed against a live game). <see cref="KillerName"/> reuses the same
/// <c>KillerName</c> field name ChampionKill/TurretKilled use (Riot's events share that
/// field across kill-type events). A class, like <see cref="KillEventRecord"/>, so a
/// stable slot can be mutated/byte-compared in place across ticks.</summary>
public sealed class InhibEventRecord
{
    public int EventId;
    public bool IsInhibKilled;
    /// <summary>True when this slot is an <c>InhibRespawned</c> event (the game reporting an
    /// inhibitor came back) rather than an <c>InhibKilled</c>. Both share this pooled record
    /// type and the <see cref="InhibId"/> field; the two flags are mutually exclusive.</summary>
    public bool IsInhibRespawned;
    public string InhibId = string.Empty;
    public string KillerName = string.Empty;
    public double EventTime;

    public void Reset()
    {
        InhibId = string.Empty;
        KillerName = string.Empty;
        ResetForParse();
    }

    /// <summary>Clears the cheap value-type fields but KEEPS InhibId/KillerName so the
    /// parser can byte-compare against them (Hard Rule #2) — see
    /// <see cref="GameSnapshot.InhibEvents"/>.</summary>
    public void ResetForParse()
    {
        EventId = 0;
        IsInhibKilled = false;
        IsInhibRespawned = false;
        EventTime = 0;
    }

    public void CopyTo(InhibEventRecord dest)
    {
        dest.EventId = EventId;
        dest.IsInhibKilled = IsInhibKilled;
        dest.IsInhibRespawned = IsInhibRespawned;
        dest.InhibId = InhibId;
        dest.KillerName = KillerName;
        dest.EventTime = EventTime;
    }
}

/// <summary>One TurretKilled entry parsed from the Live Client <c>events</c> payload
/// (patch 15.1 Nexus-turret respawn timer, M01 parser extension). <see cref="TurretId"/>
/// is the raw Riot identifier carried on the event's own <c>TurretKilled</c> field — it
/// encodes team/lane/position, kept as-is rather than decoded (the exact format is NOT
/// confirmed against a live game; community sources show both <c>Turret_T1_C_07_A</c> and
/// <c>Turret_TChaos_L1_P2_&lt;id&gt;</c> shapes). The domain layer
/// (<see cref="LiveClientEventPublisher"/>) filters to the two Nexus turrets. Same
/// append-only slot-reuse contract as <see cref="InhibEventRecord"/>.</summary>
public sealed class TurretEventRecord
{
    public int EventId;
    public bool IsTurretKilled;
    public string TurretId = string.Empty;
    public string KillerName = string.Empty;
    public double EventTime;

    public void Reset()
    {
        TurretId = string.Empty;
        KillerName = string.Empty;
        ResetForParse();
    }

    /// <summary>Clears the cheap value-type fields but KEEPS TurretId/KillerName so the
    /// parser can byte-compare against them (Hard Rule #2) — see
    /// <see cref="GameSnapshot.TurretEvents"/>.</summary>
    public void ResetForParse()
    {
        EventId = 0;
        IsTurretKilled = false;
        EventTime = 0;
    }

    public void CopyTo(TurretEventRecord dest)
    {
        dest.EventId = EventId;
        dest.IsTurretKilled = IsTurretKilled;
        dest.TurretId = TurretId;
        dest.KillerName = KillerName;
        dest.EventTime = EventTime;
    }
}
