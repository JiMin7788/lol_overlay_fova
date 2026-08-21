using Overlay.Core.ChampionDb;

namespace Overlay.Core.Combo;

/// <summary>
/// Turns a champion's M11 BIN spell formula (<see cref="SkillData.SpellCalculations"/> +
/// <see cref="SkillData.DataValues"/>) into a real damage number for a combo skill node,
/// using the live attacker's stats. This is where "총 데미지: 0" becomes a real value:
/// the combo palette stores skill nodes as zero-damage templates (BIN coefficients are
/// formula trees, not flat ratios), so the actual damage is computed here at combo run
/// time via <see cref="FormulaInterpreter"/>.
///
/// ─── THE mStat → stat MAPPING (the research problem) ─────────────────────────────────
/// A BIN calculation part references a game stat by an integer <see cref="CalculationPart.Stat"/>
/// (<c>mStat</c>) that Riot does not publish a name table for. Reading the self-describing
/// <c>mDataValue</c> names across the 5 cached champions (Aatrox, Ahri, Annie, Zed, Jinx)
/// establishes the mapping the resolver below encodes:
///
///   id 0  → Ability Power.  Every AP-scaling part in the 5 champions omits <c>mStat</c>
///           entirely (parser defaults it to 0, see <see cref="CalculationPart.Stat"/> ?? 0)
///           and its DataValue is literally named an AP ratio: Annie/Ahri "APRatio",
///           Ahri R "rAPCoefficient", Annie R "TibbersAttackAPRatio". So id 0 = AP.
///   id 1  → Armor. Confirmed across the full 173-champion cached BIN set: every id-1 part's
///           DataValue is Armor-named ("ArmorRatio", "BonusArmorRatio", "ArmorDamageValue",
///           "DamageArmorRatio", ...). A live value is available (<see
///           cref="ActivePlayerStats.Armor"/>), so it is mapped.
///   id 2  → Attack Damage.  Every AD-scaling part carries <c>mStat</c>=2 with an
///           AD-named DataValue: Aatrox "QTotalADRatio"/"WTotalADRatio", Zed "ADRatio"/
///           "BonusADRatio", Jinx "RocketTAD"/"ADRatio". So id 2 = AttackDamage.
///   id 4  → Attack Speed (§48 fix). Confirmed via Akshan E (<c>DamageToDeal</c>'s mMultiplier:
///           mStat=4, mStatFormula=2, mDataValue="AttackSpeedCoefficient"=0.30 — wiki "increased
///           by 30% per 100% bonus attack speed") and Aphelios Severum Q (<c>NumAttacks</c>:
///           mStat=4, mStatFormula=2, mDataValue="ASCoeff"). 12 champion BINs reference it
///           (akshan, aphelios, belveth, garen, jhin, kaisa, kalista, katarina, monkeyking,
///           varus, viego, volibear). TOTAL = the live final attacks/sec
///           (<see cref="ActivePlayerStats.AttackSpeed"/>); BONUS = the RATIO total/base − 1
///           (NOT an attacks/sec delta — AS formulas consume bonus AS as a fraction, and
///           per-level AS growth counts as BONUS in game, so live-total over the champion's flat
///           base AS captures items and levels together). Both curated consumers were silently
///           dead before this mapping (KeyNotFoundException → hit dropped to 0).
///   id 6  → Magic Resist. Confirmed the same way as id 1: every id-6 part's DataValue is
///           MR-named ("MRRatio", "BonusMRRatio", "MagicResistRatio", "DamageMRRatio", ...).
///           Mapped to the live <see cref="ActivePlayerStats.MagicResist"/>.
///   id 7  → Movement Speed. Confirmed via Janna's passive Tailwind (TailwindSelf.BonusDamage:
///           mStat=7, mStatFormula=2, mDataValue="MSBonusMagicDamage"=0.30), matching the live
///           tooltip "bonus magic damage equal to 30% of her bonus movement speed". Mapped to
///           the live <see cref="ActivePlayerStats.MoveSpeed"/> (bonus = total minus base, same
///           split as AD/Armor/MR — see <see cref="BuildStatResolver"/>).
///   id 12 → (Bonus) Health. Confirmed via Tahm Kench's passive "An Acquired Taste"
///           (<c>TahmKenchPassive.TotalDamage</c>): <c>mFormulaParts[1]</c> is a
///           <c>StatByCoefficientCalculationPart</c> with <c>mStat</c>=12, <c>mStatFormula</c>=2,
///           <c>mCoefficient</c>=0.04, matching the live tooltip "...plus 4% of his bonus
///           health" (magic damage over time). <c>mStatFormula</c> applies here exactly as it
///           does for AD/Armor/MR/MS: absent/other ⇒ TOTAL health, ==2 ⇒ BONUS health —
///           confirmed distinct from the passive's bonus-only usage by Tahm Kench E's
///           <c>GreyHealthMaximum</c> (<c>mStat</c>=12, coefficient 3.0, NO <c>mStatFormula</c>),
///           i.e. Devour's grey-health cap scales off TOTAL max health, not bonus. Mapped to
///           the live <see cref="ActivePlayerStats.MaxHealth"/>; bonus health is TOTAL minus the
///           champion's base+per-level HP (<see cref="ChampionBaseStats.Hp"/> +
///           <see cref="ChampionStatsPerLevel.Hp"/>*(level-1)) at <paramref name="level"/>, the
///           same pattern as bonus AD/Armor/MR/MS below.
///   else  → UNMAPPED. Ids observed but with no confirmed name AND/OR no live-snapshot stat
///           to back them (5 = unconfirmed crit mod, 10/13-31 = assorted unconfirmed;
///           8/9 = crit chance/damage, mapped to the §26 0.0 no-crit floor) are NOT silently resolved
///           to 0 — <see cref="BuildStatResolver"/> throws <see cref="KeyNotFoundException"/>
///           so a formula that actually needs one of them fails loudly and the caller drops
///           that calc/curation (see <see cref="ComputeNodeDamage"/>/<see
///           cref="ComputeCalcDamage"/> catch clauses) instead of silently computing a
///           wrong-but-plausible number with a phantom 0.
///
/// ─── TOTAL vs BONUS AD: resolved via mStatFormula ─────────────────────────────────────
/// Both total-AD and bonus-AD scalings share <c>mStat</c>=2; whether a part is total or
/// bonus lives in a *separate* field, <c>mStatFormula</c> (=2 means bonus AD, absent means
/// total AD — e.g. Aatrox Q QTotalADRatio has no mStatFormula = total AD; Zed Q
/// BonusADRatio has mStatFormula=2 = bonus AD). <see cref="ChampionBinParser"/> now captures
/// it into <see cref="CalculationPart.StatFormula"/> and <see cref="FormulaInterpreter"/>
/// forwards it to the resolver, so id 2 resolves to BONUS AD when the formula is 2 and to
/// TOTAL AD otherwise. Bonus AD is derived from public data as total AD minus base AD
/// (<see cref="ChampionBaseStats.Ad"/> + <see cref="ChampionStatsPerLevel.Ad"/>*(level-1));
/// the API exposes no bonus-AD field directly. This makes Zed Q/E, Jinx R accurate rather
/// than the former documented over-estimate.
/// </summary>
public static class SkillDamage
{
    /// <summary>mStat id for Ability Power (see class remarks): AP parts omit mStat and the
    /// parser defaults the id to 0.</summary>
    private const int StatAbilityPower = 0;

    /// <summary>mStat id for Armor (see class remarks). Resolved to TOTAL or BONUS Armor
    /// depending on the part's <c>mStatFormula</c> (see <see cref="StatFormulaBonusAd"/> — the
    /// same "2 = bonus" marker Riot uses for AD also applies here, e.g. K'Sante P
    /// <c>MaxHealthDamagePercent</c>'s Armor term).</summary>
    private const int StatArmor = 1;

    /// <summary>mStat id for Attack Damage (see class remarks). Resolved to TOTAL or BONUS AD
    /// depending on the part's <c>mStatFormula</c> (see <see cref="StatFormulaBonusAd"/>).</summary>
    private const int StatAttackDamage = 2;

    /// <summary>mStat id for Attack Speed (§48, see class remarks). TOTAL = live final
    /// attacks/sec; BONUS = the RATIO total/base − 1 (AS formulas consume bonus AS as a
    /// fraction, e.g. Akshan E's ×(1 + 0.3 × bonusAS)).</summary>
    private const int StatAttackSpeed = 4;

    /// <summary>mStat id for Magic Resist / spellblock (see class remarks). Resolved to TOTAL or
    /// BONUS MR depending on the part's <c>mStatFormula</c> (see <see cref="StatFormulaBonusAd"/>).</summary>
    private const int StatMagicResist = 6;

    /// <summary>mStat id for Movement Speed. Confirmed via Janna's passive Tailwind
    /// (<c>TailwindSelf.BonusDamage</c>): <c>mStat</c>=7, <c>mStatFormula</c>=2 (bonus),
    /// <c>mDataValue</c>="MSBonusMagicDamage"=0.30 — matching the live tooltip "deals bonus
    /// magic damage equal to 30% of her bonus movement speed". Resolved to TOTAL or BONUS MS
    /// depending on <c>mStatFormula</c> (same "2 = bonus" marker reused from AD/Armor/MR).
    /// Live value is <see cref="ActivePlayerStats.MoveSpeed"/> (Live Client
    /// <c>activePlayer.championStats.moveSpeed</c>, the final effective total). Bonus MS is
    /// TOTAL minus base (<see cref="ChampionBaseStats.Ms"/>) — MS has no per-level base growth
    /// in Data Dragon (<see cref="ChampionStatsPerLevel"/> carries no Ms field), unlike
    /// AD/Armor/MR.</summary>
    private const int StatMoveSpeed = 7;

    /// <summary>mStat id for (Bonus) Health (see class remarks). Confirmed via Tahm Kench's
    /// passive <c>TotalDamage</c> (<c>mStatFormula</c>=2 ⇒ bonus health, matching the live
    /// tooltip's "4% of his bonus health") and his E's <c>GreyHealthMaximum</c> (no
    /// <c>mStatFormula</c> ⇒ total health). Resolved to TOTAL or BONUS Health depending on the
    /// part's <c>mStatFormula</c> (see <see cref="StatFormulaBonusAd"/>), same as AD/Armor/MR/MS.</summary>
    private const int StatHealth = 12;

    /// <summary>(§12②) BIN mStat 8 = critical-strike CHANCE, 9 = critical-strike DAMAGE multiplier.
    /// Observed only inside an ABILITY's crit mMultiplier — <c>1 + CritMod × critChance(8) ×
    /// (critDamage(9) − 1)</c> — on the four abilities whose BIN calc can crit (Kindred E BaseBiteDamage,
    /// Caitlyn R RTotalDamage, Akshan R DamagePerBulletWithCrit, Tristana E ActiveDamage). This tool
    /// models ABILITY crit as NO crit: the engine only crits auto-attacks (<see cref="ComboNode"/>'s
    /// <c>CanCrit</c> is Aa-only), and RangeMin must stay the conservative no-crit floor (P2), so baking
    /// an expected crit into an ability would both diverge from that convention and lift the floor. Both
    /// ids therefore resolve to the crit-NEUTRAL value (chance 0, damage-multiplier 1), which collapses
    /// the mMultiplier to 1 → the calc yields its BIN no-crit base instead of throwing (previously it fell
    /// back to the tooltip heuristic). Live expected-crit for crittable abilities is a possible future
    /// enhancement, but only alongside a max-track crit treatment that keeps the floor honest.</summary>
    private const int StatCritChance = 8;
    private const int StatCritDamage = 9;

    /// <summary>BIN <c>mStatFormula</c> value that marks a stat part as scaling on the BONUS
    /// (post-item) amount of whichever stat it names, rather than the TOTAL amount (absent/other
    /// ⇒ total). Confirmed for AD (id 2); K'Sante P's Armor/MR terms (ids 1/6) carry the same
    /// value with the same "bonus" meaning, so it is reused rather than duplicated per stat.</summary>
    private const int StatFormulaBonusAd = 2;

    /// <summary>Substrings that mark a spell calculation as NOT the primary player-vs-champion
    /// damage (minion/monster variants, or tooltip-only edge multipliers).</summary>
    private static readonly string[] NonPrimaryMarkers = { "minion", "monster", "mono", "tooltip", "edge" };

    /// <summary>
    /// Factory for the <c>Func&lt;int,int?,double&gt;</c> that <see cref="FormulaInterpreter"/>
    /// calls to resolve a BIN <c>mStat</c> id (plus its <c>mStatFormula</c>) to a live value:
    /// id 0 → AP, id 1 → total Armor (or BONUS Armor when <c>mStatFormula</c>==2), id 2 → total AD
    /// (or BONUS AD when <c>mStatFormula</c>==2), id 6 → total Magic Resist (or BONUS MR when
    /// <c>mStatFormula</c>==2), id 7 → total Move Speed (or BONUS MS when <c>mStatFormula</c>==2),
    /// id 12 → total Health (or BONUS Health when <c>mStatFormula</c>==2).
    /// Any other id has no confirmed mapping and/or no live-snapshot
    /// stat to back it, so it throws <see cref="KeyNotFoundException"/> (see class remarks)
    /// rather than silently resolving to 0 — the caller treats that as "cannot resolve, skip"
    /// (see <see cref="ComputeNodeDamage"/>/<see cref="ComputeCalcDamage"/>). Bonus AD/Armor/MR/Health
    /// is each total stat minus its base stat (<paramref name="champion"/> base + per-level at
    /// <paramref name="level"/>), floored at 0 (additive fix: ids 1/6 previously ignored
    /// <c>mStatFormula</c> and always returned the TOTAL stat, which under-supported K'Sante P's
    /// <c>MaxHealthDamagePercent</c> Armor/MR terms — this does not change any calc that doesn't
    /// carry <c>mStatFormula</c>==2 on an id-1/id-6 part, so every other champion's curated numbers
    /// are unaffected). Adding id 12 (bonus-health ratio) is likewise additive: any curated calc
    /// that references a BIN formula part with <c>mStat</c>=12 previously threw
    /// <see cref="KeyNotFoundException"/> (caught, hit dropped/fell back to 0) — it now resolves to
    /// a real number instead. This affects more than Tahm Kench's passive (grepping the cached BIN
    /// set found id 12 also used by, among others, Braum Q, Gnar E, Maokai E, Shen E, Volibear W,
    /// and Zac Q's curated calcs) — those champions' curated hits were silently under-computing
    /// before this fix and now compute correctly; no previously-WORKING calc is affected since no
    /// other id's resolution path changed.
    /// </summary>
    public static Func<int, int?, double> BuildStatResolver(ActivePlayerStats stats, ChampionData champion, int level)
    {
        // (§26-C fix, Cowork loop 166) Real (non-linear) per-level growth — NOT naive linear — to
        // match ComboRunner's TryResolveBase and ChampionSummary.*At (which already carry the
        // "real-growth-curve fix" for a user-reported overlay bug). A linear baseAd/Armor/Mr/Hp here
        // over-states the base at mid levels, so bonus = total − base came out too LOW, slightly
        // under-counting every bonus-AD/-resist/-HP ability term (e.g. Talon L8 AD 91: linear bonus AD
        // 1.3 vs real 5.1 → P 165 vs 173). LevelGrowth.Stat(base, perLevel, level) is the same curve
        // the defender side uses (LevelGrowth.Stat = base + perLevel·(n−1)·(0.7025 + 0.0175·(n−1))).
        double baseAd = LevelGrowth.Stat(champion.BaseStats.Ad, champion.StatsPerLevel.Ad, level);
        double bonusAd = Math.Max(0.0, stats.AttackDamage - baseAd);
        double baseArmor = LevelGrowth.Stat(champion.BaseStats.Armor, champion.StatsPerLevel.Armor, level);
        double bonusArmor = Math.Max(0.0, stats.Armor - baseArmor);
        double baseMr = LevelGrowth.Stat(champion.BaseStats.Mr, champion.StatsPerLevel.Mr, level);
        double bonusMr = Math.Max(0.0, stats.MagicResist - baseMr);
        // MS has no per-level base growth (ChampionStatsPerLevel carries no Ms field), so bonus
        // MS is simply TOTAL MS minus the champion's flat base MS.
        double bonusMs = Math.Max(0.0, stats.MoveSpeed - champion.BaseStats.Ms);
        double baseHp = LevelGrowth.Stat(champion.BaseStats.Hp, champion.StatsPerLevel.Hp, level);
        double bonusHp = Math.Max(0.0, stats.MaxHealth - baseHp);
        // (§48) Attack speed: bonus AS is a RATIO (total/base − 1), not an attacks/sec delta —
        // AS formulas consume it as a fraction (Akshan E ×(1 + 0.3×bonusAS), Severum Q's
        // NumAttacks ×(1 + ASCoeff×bonusAS)), and per-level AS growth counts as BONUS in game,
        // so live-total over the flat base captures items and levels together. Guarded so an
        // unreported live AS (0) or a base-less fixture champion yields 0, never -1/Infinity.
        double baseAs = champion.BaseStats.AttackSpeed;
        double bonusAs = baseAs > 0.0 && stats.AttackSpeed > 0.0
            ? Math.Max(0.0, stats.AttackSpeed / baseAs - 1.0)
            : 0.0;
        return (statId, statFormula) => statId switch
        {
            StatAbilityPower => stats.AbilityPower,
            StatArmor => statFormula == StatFormulaBonusAd ? bonusArmor : stats.Armor,
            StatAttackDamage => statFormula == StatFormulaBonusAd ? bonusAd : stats.AttackDamage,
            StatAttackSpeed => statFormula == StatFormulaBonusAd ? bonusAs : stats.AttackSpeed,
            StatMagicResist => statFormula == StatFormulaBonusAd ? bonusMr : stats.MagicResist,
            StatMoveSpeed => statFormula == StatFormulaBonusAd ? bonusMs : stats.MoveSpeed,
            StatHealth => statFormula == StatFormulaBonusAd ? bonusHp : stats.MaxHealth,
            // (§12②/§26 fix) Ability crit modeled as NO crit (P2 conservative floor). Both crit stats
            // resolve to a value that makes the crit CONTRIBUTION zero in EVERY BIN structure:
            //   • gated `1 + CritMod × critChance(8) × (critDamage(9) − 1)` (Kindred E, Caitlyn R,
            //     Akshan R, Tristana E): critChance→0 zeroes the product ⇒ collapses to 1, regardless
            //     of critDamage's value.
            //   • UNGATED additive `EnhancedDamageMod + StatByCoefficient(critDamage(9))` (Talon Q
            //     'CriticalDamage' — a melee enhancement with NO critChance sibling): the critDamage
            //     term is the crit contribution itself, so it must be 0. The prior value 1.0 was added
            //     straight in (1.5 + 1.0 = ×2.5 ≈ 80% over-inflation — §26). 0.0 leaves ×1.5.
            // The gated cases are unaffected (critChance→0 dominates); only the ungated additive term
            // is corrected. (Live crit-damage for guaranteed-crit enhancements — IE +30% — is a future
            // exposure, deferred with the §26-B IE runaway; keep the conservative floor here.)
            StatCritChance => 0.0,
            StatCritDamage => 0.0,
            _ => throw new KeyNotFoundException($"Unmapped BIN mStat id '{statId}' — no live stat available"),
        };
    }

    /// <summary>
    /// Picks the skill's primary player-damage calculation name, or null if the skill has
    /// none (e.g. Aatrox E/R, Zed W — dashes/steroids with no direct-damage calc). Heuristic:
    /// keep calculations whose name contains "Damage" and is not a minion/monster/mono/
    /// tooltip/edge variant, then prefer canonical "TotalDamage", else "{slot}Damage"
    /// (e.g. "QDamage"), else the first remaining candidate in BIN document order (Riot
    /// lists the primary calc first). Deterministic for the 5 cached champions.
    /// </summary>
    public static string? FindPrimaryDamageCalc(SkillData skill, string slot)
    {
        var candidates = new List<string>();
        foreach (var name in skill.SpellCalculations.Keys)
        {
            if (name.Contains("damage", StringComparison.OrdinalIgnoreCase)
                && !NonPrimaryMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(name);
            }
        }
        if (candidates.Count == 0) return null;

        return candidates.FirstOrDefault(n => n.Equals("TotalDamage", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(n => n.Equals(slot + "Damage", StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }

    /// <summary>
    /// Computes the real damage of one skill slot ("Q"/"W"/"E"/"R") for <paramref name="champion"/>
    /// using the live attacker <paramref name="stats"/>, or null when there is nothing to
    /// compute (champion lacks that slot's BIN data, the slot has no primary damage calc, or
    /// the formula references data that cannot be resolved). The evaluation rank is the
    /// player's REAL in-game rank for the slot (<see cref="ActivePlayerStats.AbilityQ"/> etc.),
    /// so damage reflects actual skill levels — see <see cref="ChooseRank"/> for the rank-1
    /// floor and the max-rank fallback used when no live ability data is available.
    /// <paramref name="level"/> (the attacker's champion level) is used only to derive base AD
    /// for the bonus-AD split.
    /// </summary>
    public static double? ComputeNodeDamage(ChampionData champion, string slot, ActivePlayerStats stats, int level = 1)
    {
        if (!champion.Skills.TryGetValue(slot, out var skill)) return null;

        var calcName = FindPrimaryDamageCalc(skill, slot);
        if (calcName is null) return null;

        int rank = ChooseRank(skill, slot, stats);
        // (guideline 2026-07-16) rank 0 = a canonical Q/W/E/R not yet leveled → cannot be cast, no
        // damage (see ChooseRank). Return null so the node contributes nothing.
        if (rank == 0) return null;
        var resolver = BuildStatResolver(stats, champion, level);
        try
        {
            // (M25 §12③) championLevel indexes "based on level" ByCharLevel* parts by CHAMPION level,
            // not ability rank (which under-counts a level-scaling ability at levels above its rank).
            return FormulaInterpreter.Evaluate(skill, calcName, rank, resolver, stats.ResourceMax,
                championLevel: Math.Max(1, level));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            // Formula referenced a DataValue/calc that isn't present, or an unrecognized
            // part kind — fall back to the node's template damage rather than crash.
            return null;
        }
    }

    /// <summary>
    /// Evaluates a SPECIFIC BIN calculation (<paramref name="calcName"/>) for one skill slot,
    /// at the player's real ability rank — the curated-JSON path (see
    /// <see cref="SkillDamageDb"/>), which names the exact single-target calc per hit rather
    /// than relying on <see cref="FindPrimaryDamageCalc"/>'s heuristic. Returns null when the
    /// champion lacks that slot, or the named calc/DataValue can't be resolved (e.g. a BIN
    /// quirk) — the caller then drops that hit rather than crash.
    /// </summary>
    public static double? ComputeCalcDamage(
        ChampionData champion, string slot, string calcName, ActivePlayerStats stats, int level = 1,
        int stackCount = 0, string? rankSlot = null)
    {
        if (!champion.Skills.TryGetValue(slot, out var skill)) return null;
        if (!skill.SpellCalculations.ContainsKey(calcName)) return null;

        // (golden Hwei round 2026-07-26) rankSlot: the CURATED slot the hit belongs to, when `slot`
        // is a BinSpell data override (e.g. slot="HweiEQ" carrying the calc, rankSlot="E" carrying
        // the rank). A raw BIN object name has no rank of its own — without this, every
        // BinSpell-routed hit silently evaluated at the rank-1 floor regardless of the player's
        // real skill level (invisible in the Jayce golden, whose ranks were all 1). A binSpell that
        // names a CANONICAL slot (or "P") keeps ITS OWN rank: it is a real leveled ability being
        // referenced (Illaoi R slams reuse Q's TentacleDamageTotal AT Q'S RANK), not a rankless
        // sub-spell object.
        string rs = IsCanonicalSlot(slot) || slot.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? slot : (rankSlot ?? slot);
        // A PASSIVE ("P") has no ability rank — its damage scales by the champion's LEVEL. Passive
        // calcs express that via ByCharLevel* parts and level-indexed DataValues, so the champion
        // level is the correct "rank" to evaluate them at (Q/W/E/R still use the real skill rank).
        int rank = rs.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, level)
            : ChooseRank(skill, rs, stats);
        // (guideline 2026-07-16) rank 0 = a canonical Q/W/E/R not yet leveled → no cast, no damage
        // (and its skill-passive does not apply); drop the hit. Never 0 for "P" (uses champion level).
        if (rank == 0) return null;
        var resolver = BuildStatResolver(stats, champion, level);
        try
        {
            // (M25 §11.G) stackCount feeds BuffCounter per-stack terms (Nasus Q Siphoning stacks, …);
            // 0 (default) = the un-stacked floor, so non-stack calcs are unaffected.
            // (M25 §12③) championLevel indexes ByCharLevel* "based on level" parts by champion level.
            return FormulaInterpreter.Evaluate(skill, calcName, rank, resolver, stats.ResourceMax, stackCount,
                championLevel: Math.Max(1, level));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a FLAT (non-%HP) BIN DataValue by name at the correct rank — for a hit whose
    /// real number is a plain rank-indexed <c>SkillData.DataValues</c> entry with no
    /// separately-resolvable <see cref="SkillData.SpellCalculations"/> entry of its own (see
    /// <see cref="SkillHit.FlatDataValue"/>'s own doc comment for the motivating Garen R case).
    /// Same rank rule as <see cref="ComputeCalcDamage"/>. Returns null when the champion lacks
    /// the slot or the DataValue can't be resolved (caller drops the hit, documented).
    /// </summary>
    public static double? ComputeFlatDataValue(
        ChampionData champion, string slot, string dataValueName, ActivePlayerStats stats, int level = 1,
        string? rankSlot = null)
    {
        if (!champion.Skills.TryGetValue(slot, out var skill)) return null;

        // rankSlot: curated slot for rank purposes when `slot` is a rankless BinSpell override —
        // see ComputeCalcDamage (a canonical/"P" data slot keeps its own rank).
        string rs = IsCanonicalSlot(slot) || slot.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? slot : (rankSlot ?? slot);
        int rank = rs.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, level)
            : ChooseRank(skill, rs, stats);
        // (guideline 2026-07-16) rank 0 = a canonical Q/W/E/R not yet leveled → no damage (see ChooseRank).
        if (rank == 0) return null;
        try
        {
            return FormulaInterpreter.EvaluateDataValue(skill, dataValueName, rank);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a percent-of-HP FRACTION for one skill slot LIVE from a BIN DataValue, at the
    /// same rank <see cref="ComputeCalcDamage"/> uses (the passive "P" scales by champion level;
    /// Q/W/E/R use the player's real ability rank), so a %HP skill/passive tracks the current
    /// patch and is correct at every rank instead of a baked literal (T7).
    ///
    /// Normalizes the two BIN encodings of a percent DataValue: a FRACTION (Vayne W
    /// <c>MaxHealthRatio</c> = 0.05..0.11, value &lt; 1) is returned as-is; a WHOLE-PERCENT value
    /// (Brand P <c>PercentHealthDamage</c> = 2.0, value ≥ 1) is divided by 100 → 0.02. A per-cast
    /// %maxHP fraction is always well below 1, and every whole-percent encoding is ≥ 1, so the
    /// <c>&gt;= 1</c> test cleanly separates the two without a per-champion units flag. Returns
    /// null when the champion lacks the slot or the DataValue can't be resolved (caller falls back
    /// to the hit's literal <c>hpPercent</c>, if any).
    /// </summary>
    public static double? ResolveHpPercent(
        ChampionData champion, string slot, string dataValueName, ActivePlayerStats stats, int level = 1,
        string? rankSlot = null)
    {
        if (!champion.Skills.TryGetValue(slot, out var skill)) return null;

        // rankSlot: curated slot for rank purposes when `slot` is a rankless BinSpell override —
        // see ComputeCalcDamage (a canonical/"P" data slot keeps its own rank).
        string rs = IsCanonicalSlot(slot) || slot.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? slot : (rankSlot ?? slot);
        int rank = rs.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, level)
            : ChooseRank(skill, rs, stats);
        // (guideline 2026-07-16) rank 0 = a canonical Q/W/E/R not yet leveled → no damage (see ChooseRank).
        if (rank == 0) return null;
        try
        {
            double raw = FormulaInterpreter.EvaluateDataValue(skill, dataValueName, rank);
            return raw >= 1.0 ? raw / 100.0 : raw;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a percent-of-HP FRACTION for one skill slot LIVE from a full BIN
    /// <c>GameCalculation</c> formula tree via <see cref="FormulaInterpreter.Evaluate"/> — for %HP
    /// mechanics whose fraction is not a flat <see cref="SkillData.DataValues"/> array entry but
    /// lives only inside a calculation (Skarner P "PercentHealthDamage" =
    /// <c>ByCharLevelInterpolation(5.0→9.0) × mMultiplier(0.01)</c>; there is no matching
    /// <c>DataValues</c> entry for <see cref="ResolveHpPercent"/> to find). Uses the same rank rule
    /// (passive "P" scales by champion level; Q/W/E/R use the real ability rank) and the same live
    /// <see cref="BuildStatResolver"/> as <see cref="ComputeCalcDamage"/>, so a calc-sourced %HP
    /// fraction that also references a stat term (e.g. K'Sante's) resolves correctly. Applies the
    /// same ≥1.0-means-whole-percent normalization as <see cref="ResolveHpPercent"/> for
    /// consistency, though every currently known calc-sourced case already evaluates to a sub-1
    /// fraction (Skarner's <c>mMultiplier</c> is baked into the calc itself). Returns null when the
    /// slot/calc can't be resolved (caller falls back to the hit's literal <c>hpPercent</c>, if any).
    /// </summary>
    public static double? ResolveHpPercentCalc(
        ChampionData champion, string slot, string calcName, ActivePlayerStats stats, int level = 1,
        int stackCount = 0, string? rankSlot = null)
    {
        if (!champion.Skills.TryGetValue(slot, out var skill)) return null;
        if (!skill.SpellCalculations.ContainsKey(calcName)) return null;

        // rankSlot: curated slot for rank purposes when `slot` is a rankless BinSpell override —
        // see ComputeCalcDamage (a canonical/"P" data slot keeps its own rank).
        string rs = IsCanonicalSlot(slot) || slot.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? slot : (rankSlot ?? slot);
        int rank = rs.Equals("P", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(1, level)
            : ChooseRank(skill, rs, stats);
        // (guideline 2026-07-16) rank 0 = a canonical Q/W/E/R not yet leveled → no damage (see ChooseRank).
        if (rank == 0) return null;
        var resolver = BuildStatResolver(stats, champion, level);
        try
        {
            // (M25 §12③) championLevel indexes ByCharLevel* "based on level" parts by champion level
            // (e.g. Ornn W BrittlePercentMaxHPCalc's level-scaled %HP fraction).
            // (M25 §11.G) stackCount feeds a BuffCounter per-stack term in a %HP fraction (e.g. Kindred E
            // PercentBiteDamage = 5% + 0.5% per Mark of missing HP); 0 (default) = the un-stacked base.
            double raw = FormulaInterpreter.Evaluate(skill, calcName, rank, resolver, stats.ResourceMax,
                stackCount, championLevel: Math.Max(1, level));
            return raw >= 1.0 ? raw / 100.0 : raw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the evaluation rank for <paramref name="slot"/>, preferring the player's REAL
    /// in-game rank from <paramref name="stats"/>:
    /// <list type="bullet">
    /// <item>Real rank ≥ 1 → use it (the accurate case this fix enables).</item>
    /// <item>Real rank 0 but live ability data IS present (any slot leveled) → floor to rank 1.
    /// A rank-0 skill can't actually be cast, but a rank-1 value is a sane, non-zero preview
    /// for a not-yet-leveled skill in the combo.</item>
    /// <item>No live ability data at all (every slot 0, e.g. abilities block absent /
    /// spectator / a synthetic stats object) → documented MVP fallback of MaxRank
    /// (<see cref="SkillData.MaxRank"/>, else 3 for R / 5 for basic abilities).</item>
    /// </list>
    /// The chosen rank is further clamped to the DataValue array bounds by
    /// <see cref="FormulaInterpreter"/>.
    /// </summary>
    private static int ChooseRank(SkillData skill, string slot, ActivePlayerStats stats)
    {
        int real = RealRankForSlot(slot, stats);
        // (golden Hwei round 2026-07-26) An extra-form/sub-cast slot named after its parent
        // ("QCannon", "QMega", "QQ"/"QW"/"QE") is CAST at that parent canonical slot's rank —
        // inherit it when the parent is leveled. This only ever RAISES the rank (an unleveled or
        // underivable parent falls through to the pre-existing floor/fallback below), so no hit
        // that resolved before resolves differently at rank 1.
        if (real == 0 && !IsCanonicalSlot(slot) && slot.Length > 1)
            real = RealRankForSlot(slot[..1], stats);
        if (real >= 1) return real;
        // (guideline 2026-07-16) A CANONICAL Q/W/E/R slot at real rank 0 is NOT leveled: it cannot be
        // cast and its skill-passive does not apply, so it must deal NO damage. Return the sentinel 0
        // (every Compute*/Resolve* method above treats a chosen rank of 0 as "no damage" → null) INSTEAD
        // of the former rank-1 preview floor. Applied only when live ability data IS present (we KNOW
        // this slot is unleveled); with NO ability data at all (combo-editor preview / spectator) the
        // ranks are unknown — not confirmed 0 — so the MaxRank fallback below is kept. NON-canonical
        // slots (multiform form/weapon keys like "QCannon"/"QMega", reached via SkillHit.BinSpell) are
        // NOT subject to this: RealRankForSlot returns 0 for them only because they are not a base slot,
        // never because they are unleveled, so they keep the rank-1 floor and resolve their real
        // alternate-form damage rather than being wrongly zeroed.
        if (HasAbilityData(stats)) return IsCanonicalSlot(slot) ? 0 : 1;
        return skill.MaxRank > 0 ? skill.MaxRank : (slot.Equals("R", StringComparison.OrdinalIgnoreCase) ? 3 : 5);
    }

    /// <summary>True when <paramref name="slot"/> is one of the four canonical castable ability slots
    /// Q/W/E/R (case-insensitive). Used by <see cref="ChooseRank"/> to distinguish a genuinely-unleveled
    /// canonical skill (real rank 0 → no damage, per the rank-0 guideline) from a non-canonical
    /// multiform/form key (e.g. "QCannon"), whose 0 from <see cref="RealRankForSlot"/> means only "not a
    /// base slot", never "unleveled". The passive "P" never reaches here (its callers use champion level
    /// as the rank), so it is intentionally not treated as canonical for this purpose.</summary>
    private static bool IsCanonicalSlot(string slot) => slot.ToUpperInvariant() switch
    {
        "Q" or "W" or "E" or "R" => true,
        _ => false,
    };

    /// <summary>The player's real rank for a slot ("Q"/"W"/"E"/"R"), or 0 for an unknown slot
    /// or a slot not yet leveled.</summary>
    private static int RealRankForSlot(string slot, ActivePlayerStats stats) => slot.ToUpperInvariant() switch
    {
        "Q" => stats.AbilityQ,
        "W" => stats.AbilityW,
        "E" => stats.AbilityE,
        "R" => stats.AbilityR,
        _ => 0,
    };

    /// <summary>True when the snapshot carried real ability ranks (at least one slot leveled).
    /// Distinguishes "abilities parsed, this slot just isn't leveled" (rank-1 floor) from
    /// "no ability data at all" (max-rank fallback).</summary>
    private static bool HasAbilityData(ActivePlayerStats stats)
        => stats.AbilityQ > 0 || stats.AbilityW > 0 || stats.AbilityE > 0 || stats.AbilityR > 0;
}
