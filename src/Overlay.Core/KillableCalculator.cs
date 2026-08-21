namespace Overlay.Core;

/// <summary>
/// Deterministic one-combo KILLABLE damage calculator (DA-001).
///
/// Pure math: given an attacker's offensive profile and a target's defensive
/// profile, it computes the attacker's available single-rotation burst, splits
/// it into physical / magic / true components, applies the target's effective
/// resists (after penetration) and any shield, and reports whether the target
/// dies in one combo plus the HP / overkill margin.
///
/// No UI, no polling, no I/O, no voice (data-algo SKILL "Boundary"). It consumes
/// only Overlay.Core snapshot types + <see cref="StaticGameData"/>; it never
/// touches the Live Client Data API or WPF.
///
/// This is NOT the 0.5s hot path, so it favours clarity/correctness over zero
/// allocation; it still avoids unnecessary heap churn (no LINQ, value-type result).
/// </summary>
public sealed class KillableCalculator
{
    private readonly StaticGameData _data;

    public KillableCalculator(StaticGameData data)
        => _data = data ?? throw new ArgumentNullException(nameof(data));

    // --- LoL formula constants (kept here as the engine's own math, not balance
    //     data; champion/item balance numbers live in the JSON per Hard Rule #4).

    /// <summary>Standard per-level stat growth multiplier. League grows a base stat
    /// by perLevel * (level-1) * (0.7025 + 0.0175*(level-1)), i.e. a slightly
    /// super-linear curve, not flat perLevel*level. Source: Riot stat growth formula
    /// (community-documented "growth statistic" coefficients).</summary>
    private const double GrowthA = 0.7025;
    private const double GrowthB = 0.0175;

    /// <summary>Lethality -> flat armor pen scales with the TARGET's level:
    /// flatPen = lethality * (0.6 + 0.4 * targetLevel / 18). Source: Riot lethality
    /// definition.</summary>
    private const double LethalityMinFactor = 0.6;
    private const double LethalityLevelFactor = 0.4;
    private const int MaxChampLevel = 18;

    /// <summary>Damage multiplier from a resist value: positive resist reduces, and
    /// negative resist (over-penetration) amplifies, per League's resist formula.</summary>
    public static double ResistMultiplier(double effectiveResist)
        => effectiveResist >= 0
            ? 100.0 / (100.0 + effectiveResist)
            : 2.0 - 100.0 / (100.0 - effectiveResist);

    /// <summary>Apply the standard quadratic per-level growth curve.</summary>
    private static double GrowStat(double baseValue, double perLevel, int level)
    {
        if (level <= 1) return baseValue;
        int n = level - 1;
        return baseValue + perLevel * n * (GrowthA + GrowthB * n);
    }

    /// <summary>
    /// Compute whether <paramref name="attacker"/> can kill <paramref name="target"/>
    /// in one combo. The attacker's live championStats (if supplied) are preferred
    /// for AD/AP; otherwise stats are derived from static base+level. Target defenses
    /// are derived from static base+level+items (the Live scoreboard exposes level +
    /// items but not live resists), unless live target stats are supplied.
    /// </summary>
    public KillableResult Evaluate(
        in AttackerInput attacker, in TargetInput target,
        IReadOnlyList<ExecuteRule>? executeRules = null,
        ExecuteContext executeContext = default)
    {
        var atkChamp = _data.GetChampionOrDefault(attacker.ChampionName);
        var tgtChamp = _data.GetChampionOrDefault(target.ChampionName);

        // ---- Attacker offensive stats ----
        // Prefer live championStats when the attacker is the active player; the
        // Live Client API only exposes live stats for the active player.
        double totalAd = attacker.LiveAttackDamage
            ?? GrowStat(atkChamp.BaseAttackDamage, atkChamp.AttackDamagePerLevel, attacker.Level)
               + SumItemStat(attacker.ItemIds, attacker.ItemCount, ItemStat.AttackDamage);
        double baseAd = GrowStat(atkChamp.BaseAttackDamage, atkChamp.AttackDamagePerLevel, attacker.Level);
        double bonusAd = Math.Max(0, totalAd - baseAd);
        double totalAp = attacker.LiveAbilityPower
            ?? SumItemStat(attacker.ItemIds, attacker.ItemCount, ItemStat.AbilityPower);

        // Attacker penetration sourced from items.
        double lethality = SumItemStat(attacker.ItemIds, attacker.ItemCount, ItemStat.Lethality);
        double flatArmorPen = LethalityToFlatPen(lethality, target.Level);
        double percentArmorPen = SumPercentPen(attacker.ItemIds, attacker.ItemCount, ItemStat.PercentArmorPen);
        double flatMagicPen = SumItemStat(attacker.ItemIds, attacker.ItemCount, ItemStat.FlatMagicPen);
        double percentMagicPen = SumPercentPen(attacker.ItemIds, attacker.ItemCount, ItemStat.PercentMagicPen);

        // ---- Target defensive stats ----
        double targetArmor = target.LiveArmor
            ?? GrowStat(tgtChamp.BaseArmor, tgtChamp.ArmorPerLevel, target.Level)
               + SumItemStat(target.ItemIds, target.ItemCount, ItemStat.Armor);
        double targetMr = target.LiveMagicResist
            ?? GrowStat(tgtChamp.BaseMagicResist, tgtChamp.MagicResistPerLevel, target.Level)
               + SumItemStat(target.ItemIds, target.ItemCount, ItemStat.MagicResist);

        double targetMaxHp = target.LiveMaxHealth
            ?? GrowStat(tgtChamp.BaseHealth, tgtChamp.HealthPerLevel, target.Level)
               + SumItemStat(target.ItemIds, target.ItemCount, ItemStat.Health);

        // currentHP is the compare target (SKILL: never max HP). If unknown, assume full.
        double targetCurrentHp = target.CurrentHealth >= 0 ? target.CurrentHealth : targetMaxHp;

        // Effective resists after penetration. Percent pen applies first, then flat
        // (League order); flat pen cannot push a positive resist below 0.
        double effectiveArmor = ApplyPen(targetArmor, percentArmorPen, flatArmorPen);
        double effectiveMr = ApplyPen(targetMr, percentMagicPen, flatMagicPen);

        double physMult = ResistMultiplier(effectiveArmor);
        double magicMult = ResistMultiplier(effectiveMr);

        // ---- Sum the combo, split by type, then mitigate ----
        // First pass: non-amplifier components -> raw pre-mitigation totals.
        double rawPhys = 0, rawMagic = 0, rawTrue = 0;
        var abilities = atkChamp.Abilities;
        for (int i = 0; i < abilities.Length; i++)
        {
            var a = abilities[i];
            if (a.PercentOfComboDamage > 0) continue; // handled in second pass
            double dmg = a.Flat + a.PerAd * totalAd + a.PerBonusAd * bonusAd + a.PerAp * totalAp;
            switch (a.Type)
            {
                case "Physical": rawPhys += dmg; break;
                case "True": rawTrue += dmg; break;
                default: rawMagic += dmg; break; // "Magic"
            }
        }

        // Per-type mitigated base of the non-amplifier combo.
        double physMitigated = rawPhys * physMult;
        double magicMitigated = rawMagic * magicMult;
        double trueMitigated = rawTrue;
        double baseMitigated = physMitigated + magicMitigated + trueMitigated;

        // Second pass: amplifier components (e.g. Zed R) add a percent of the BASE
        // combo (not of previously-amplified output), so multiple amplifiers each
        // scale the same base instead of compounding. The bonus is distributed
        // across the phys/magic/true split in proportion to each type's share, so
        // the reported per-type damages still sum to TotalDamage.
        double amplifierFraction = 0;
        for (int i = 0; i < abilities.Length; i++)
        {
            var a = abilities[i];
            if (a.PercentOfComboDamage <= 0) continue;
            amplifierFraction += a.PercentOfComboDamage;
        }

        if (amplifierFraction > 0 && baseMitigated > 0)
        {
            double scale = 1.0 + amplifierFraction;
            physMitigated *= scale;
            magicMitigated *= scale;
            trueMitigated *= scale;
        }

        double mitigated = physMitigated + magicMitigated + trueMitigated;

        // ---- Shield / effective-HP folding (SKILL: subtract shield before compare) ----
        double shield = Math.Max(0, target.ActiveShield);
        double effectiveTargetHp = targetCurrentHp + shield;
        double postComboHp = effectiveTargetHp - mitigated;

        bool killableByDamage = mitigated >= effectiveTargetHp;

        // ---- Execute kill-line (M24 P9) — pure addition, folded via OR ----
        // A hard execute kills the target if the combo leaves it at/below the rule's HP
        // line, independent of whether raw damage alone is lethal. Kept strictly separate
        // from the damage compare above; with no rules supplied this stays inert and the
        // verdict is identical to the damage-only path (back-compat for existing callers).
        bool killableByExecute = false;
        double executeThresholdHp = 0;      // highest ACTIVE line among the supplied rules
        string? executeRuleId = null;
        if (executeRules is { Count: > 0 })
        {
            for (int i = 0; i < executeRules.Count; i++)
            {
                var rule = executeRules[i];
                if (!ExecuteEvaluator.IsActive(rule, executeContext)) continue;

                double thr = ExecuteEvaluator.ThresholdHp(
                    rule, executeContext, targetMaxHp, totalAp, bonusAd, lethality);
                if (thr > executeThresholdHp)
                {
                    executeThresholdHp = thr;
                    executeRuleId = rule.Id;
                }

                if (ExecuteEvaluator.Kills(
                        rule, executeContext, targetCurrentHp, shield, mitigated,
                        targetMaxHp, totalAp, bonusAd, lethality, target.Negation))
                    killableByExecute = true;
            }
        }

        bool killable = killableByDamage || killableByExecute;

        return new KillableResult
        {
            Killable = killable,
            KillableByExecute = killableByExecute,
            ExecuteThresholdHp = executeThresholdHp,
            ExecuteRuleId = executeRuleId,
            TotalDamage = mitigated,
            PhysicalDamage = physMitigated,
            MagicDamage = magicMitigated,
            TrueDamage = trueMitigated,
            TargetCurrentHp = targetCurrentHp,
            TargetShield = shield,
            TargetEffectiveHp = effectiveTargetHp,
            // Positive => overkill margin; negative => HP the target survives with.
            HpDelta = mitigated - effectiveTargetHp,
            TargetHpRemaining = killable ? 0 : Math.Max(0, postComboHp),
            EffectiveArmor = effectiveArmor,
            EffectiveMagicResist = effectiveMr,
            UsedDefaultAttackerProfile = ReferenceEquals(atkChamp, _data.GetChampionOrDefault(null)) && !KnownChampion(attacker.ChampionName),
            UsedDefaultTargetProfile = !KnownChampion(target.ChampionName),
        };
    }

    private bool KnownChampion(string? name)
        => !string.IsNullOrEmpty(name) && _data.Champions.ContainsKey(name);

    /// <summary>Percent pen first (multiplicative), then flat pen, floored at 0 for
    /// originally-positive resists (flat pen does not create negative resist).</summary>
    private static double ApplyPen(double resist, double percentPen, double flatPen)
    {
        double afterPercent = resist * (1.0 - percentPen);
        double afterFlat = afterPercent - flatPen;
        return afterFlat < 0 ? 0 : afterFlat;
    }

    private double LethalityToFlatPen(double lethality, int targetLevel)
    {
        if (lethality <= 0) return 0;
        int lvl = Math.Clamp(targetLevel, 1, MaxChampLevel);
        return lethality * (LethalityMinFactor + LethalityLevelFactor * lvl / MaxChampLevel);
    }

    private enum ItemStat { AttackDamage, AbilityPower, Armor, MagicResist, Health, Lethality, FlatMagicPen, PercentArmorPen, PercentMagicPen }

    private double SumItemStat(int[]? itemIds, int count, ItemStat which)
    {
        if (itemIds is null) return 0;
        double sum = 0;
        int n = Math.Min(count, itemIds.Length);
        for (int i = 0; i < n; i++)
        {
            var item = _data.GetItem(itemIds[i]);
            if (item is null) continue;
            sum += which switch
            {
                ItemStat.AttackDamage => item.AttackDamage,
                ItemStat.AbilityPower => item.AbilityPower,
                ItemStat.Armor => item.Armor,
                ItemStat.MagicResist => item.MagicResist,
                ItemStat.Health => item.Health,
                ItemStat.Lethality => item.Lethality,
                ItemStat.FlatMagicPen => item.FlatMagicPen,
                _ => 0,
            };
        }
        return sum;
    }

    /// <summary>Stacks multiple percent-pen sources multiplicatively (League rule:
    /// they don't simply add), returning the combined fraction reduced.</summary>
    private double SumPercentPen(int[]? itemIds, int count, ItemStat which)
    {
        if (itemIds is null) return 0;
        double remaining = 1.0;
        int n = Math.Min(count, itemIds.Length);
        for (int i = 0; i < n; i++)
        {
            var item = _data.GetItem(itemIds[i]);
            if (item is null) continue;
            double pct = which == ItemStat.PercentArmorPen ? item.PercentArmorPen
                       : which == ItemStat.PercentMagicPen ? item.PercentMagicPen
                       : 0;
            if (pct > 0) remaining *= (1.0 - pct);
        }
        return 1.0 - remaining;
    }

    /// <summary>Convenience overload: build attacker input from the active player's
    /// live snapshot stats and a target scoreboard entry.</summary>
    public KillableResult Evaluate(GameSnapshot snapshot, string attackerChampionName, ScoreboardEntry target)
    {
        var atk = new AttackerInput
        {
            ChampionName = attackerChampionName,
            Level = snapshot.Level,
            LiveAttackDamage = snapshot.Stats.AttackDamage,
            LiveAbilityPower = snapshot.Stats.AbilityPower,
            ItemIds = null, // active-player items not in this snapshot subset; live AD/AP already includes them
            ItemCount = 0,
        };
        var tgt = new TargetInput
        {
            ChampionName = target.ChampionName,
            Level = target.Level,
            ItemIds = target.ItemIds,
            ItemCount = target.ItemCount,
            CurrentHealth = -1, // unknown for non-active players via Live API -> assume full
        };
        return Evaluate(in atk, in tgt);
    }
}

/// <summary>Offensive profile of the would-be killer.</summary>
public readonly struct AttackerInput
{
    public string? ChampionName { get; init; }
    public int Level { get; init; }

    /// <summary>Live total AD from the active-player championStats, if available.
    /// When set, item AD is assumed already folded in (live stat is authoritative).</summary>
    public double? LiveAttackDamage { get; init; }
    public double? LiveAbilityPower { get; init; }

    /// <summary>Item ids for deriving AD/AP/penetration when live stats are absent
    /// (e.g. evaluating an enemy as the attacker). May be null.</summary>
    public int[]? ItemIds { get; init; }
    public int ItemCount { get; init; }
}

/// <summary>Defensive profile of the target.</summary>
public readonly struct TargetInput
{
    public string? ChampionName { get; init; }
    public int Level { get; init; }

    public int[]? ItemIds { get; init; }
    public int ItemCount { get; init; }

    /// <summary>Current HP to compare against (SKILL: current, never max). Negative
    /// => unknown, calc assumes full HP.</summary>
    public double CurrentHealth { get; init; }

    /// <summary>Active flat shield value (Steraks/Zhonya-style). Folded into effective HP.</summary>
    public double ActiveShield { get; init; }

    /// <summary>(M24 P9) Target-side effects that NEGATE all executes even shield-piercing
    /// ones (invuln / undying / min-HP guarantee). Default <see cref="ExecuteNegation.None"/>.
    /// Only consulted when execute rules are supplied to <see cref="KillableCalculator.Evaluate"/>.</summary>
    public ExecuteNegation Negation { get; init; }

    // Optional live override stats (used when the target IS the active player).
    public double? LiveArmor { get; init; }
    public double? LiveMagicResist { get; init; }
    public double? LiveMaxHealth { get; init; }
}

/// <summary>Result of a one-combo KILLABLE evaluation. Value type — no allocation.</summary>
public readonly struct KillableResult
{
    /// <summary>True if the target dies this combo — by raw damage OR by a hard execute
    /// kill-line (<see cref="KillableByExecute"/>).</summary>
    public bool Killable { get; init; }

    /// <summary>(M24 P9) True when a hard execute finishes the target (its post-combo health
    /// is at/below an active execute line), regardless of whether raw damage alone was lethal.
    /// Always false when no execute rules were supplied.</summary>
    public bool KillableByExecute { get; init; }

    /// <summary>(M24 P9) The highest ACTIVE execute HP line among the supplied rules (absolute
    /// HP), for the overlay's separate "≤N 확정킬" line. 0 when no active execute applies.</summary>
    public double ExecuteThresholdHp { get; init; }

    /// <summary>(M24 P9) Id of the rule that set <see cref="ExecuteThresholdHp"/>, or null.</summary>
    public string? ExecuteRuleId { get; init; }

    /// <summary>Total post-mitigation combo damage.</summary>
    public double TotalDamage { get; init; }
    public double PhysicalDamage { get; init; }
    public double MagicDamage { get; init; }
    public double TrueDamage { get; init; }

    public double TargetCurrentHp { get; init; }
    public double TargetShield { get; init; }
    /// <summary>Current HP + shield — what the combo must exceed.</summary>
    public double TargetEffectiveHp { get; init; }

    /// <summary>damage - effectiveHp. Positive => overkill margin; negative => the
    /// shortfall (how much HP the target survives with).</summary>
    public double HpDelta { get; init; }

    /// <summary>Target HP left after the combo (0 if killable).</summary>
    public double TargetHpRemaining { get; init; }

    public double EffectiveArmor { get; init; }
    public double EffectiveMagicResist { get; init; }

    /// <summary>True if a champion wasn't in the static data file and the generic
    /// fallback profile was used — consumers should treat the verdict as low-confidence.</summary>
    public bool UsedDefaultAttackerProfile { get; init; }
    public bool UsedDefaultTargetProfile { get; init; }
}
