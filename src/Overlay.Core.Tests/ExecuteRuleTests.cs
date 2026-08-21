namespace Overlay.Core.Tests;

/// <summary>
/// (M24 P9) Executable proof for the hard-execute kill-line: the pure
/// <see cref="ExecuteEvaluator"/> threshold expression + kill decision (state execute,
/// damage-brings-below, shield-pierce, negation, stack gate), the curated
/// <see cref="ExecuteEffectsDb"/> roster (present executes, absent NON-executes), and the
/// <see cref="KillableCalculator"/> integration — most importantly that supplying NO rules
/// leaves the verdict byte-identical to the pre-P9 damage-only path (no damage regression).
/// </summary>
public class ExecuteRuleTests
{
    private static ExecuteRule Percent(double @base, bool pierces = true, int gate = 0, double perStack = 0, double perRank = 0)
        => new()
        {
            Id = "t", Source = ExecuteSource.Ability, Kind = ExecuteThresholdKind.PercentMaxHp,
            Base = @base, PerStack = perStack, PerRank = perRank, PiercesShield = pierces, GateStacks = gate,
        };

    private static ExecuteRule Flat(double @base, double perStack = 0, double perLevel = 0, double perAp = 0)
        => new()
        {
            Id = "t", Source = ExecuteSource.Ability, Kind = ExecuteThresholdKind.FlatHp,
            Base = @base, PerStack = perStack, PerLevel = perLevel, PerAp = perAp, PiercesShield = true,
        };

    // ── threshold expression ─────────────────────────────────────────────────────

    [Fact]
    public void ThresholdHp_PercentMaxHp_MultipliesMaxHp()
    {
        var thr = ExecuteEvaluator.ThresholdHp(Percent(0.05), default, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0);
        Assert.Equal(100, thr);
    }

    [Fact]
    public void ThresholdHp_FlatHp_IsAbsolute_AndScalesWithStacks()
    {
        // Draven-style: threshold HP == adoration stack count.
        var thr = ExecuteEvaluator.ThresholdHp(Flat(0, perStack: 1), new ExecuteContext { Stacks = 350 }, 3000, 0, 0, 0);
        Assert.Equal(350, thr);
    }

    [Fact]
    public void ThresholdHp_CombinesRankAndStackTerms()
    {
        // Locke-style: 10% + 1%/rank-above-1 + 0.5%/seal stack. Rank 3, 20 stacks, 1000 maxHp.
        var rule = Percent(0.10, perStack: 0.005, perRank: 0.01);
        var ctx = new ExecuteContext { AbilityRank = 3, Stacks = 20 };
        var thr = ExecuteEvaluator.ThresholdHp(rule, ctx, targetMaxHp: 1000, ap: 0, bonusAd: 0, lethality: 0);
        Assert.Equal(220, thr, 3); // (0.10 + 0.02 + 0.10) * 1000
    }

    [Fact]
    public void ThresholdHp_FlatHp_AddsApAndLevelTerms()
    {
        // Zeri-P-style: 70 + 5.918*(level-1) + 0.2*AP. Level 18, AP 200.
        var thr = ExecuteEvaluator.ThresholdHp(Flat(70, perLevel: 5.918, perAp: 0.20),
            new ExecuteContext { CasterLevel = 18 }, 2500, ap: 200, bonusAd: 0, lethality: 0);
        Assert.Equal(70 + 5.918 * 17 + 0.2 * 200, thr, 3);
    }

    [Fact]
    public void ThresholdHp_BaseByLevel_ReplacesLinearBaseTerm_AndClampsOutOfRange()
    {
        // (§8) A per-level table overrides Base+PerLevel for the flat term; the other terms still add.
        var rule = new ExecuteRule
        {
            Id = "t", Source = ExecuteSource.Ability, Kind = ExecuteThresholdKind.FlatHp,
            Base = 999, PerLevel = 999,                    // must be IGNORED when BaseByLevel is present
            BaseByLevel = new double[] { 10, 20, 30 },     // levels 1,2,3
            PerAp = 0.5, PiercesShield = true,
        };
        // Level 2 -> table[1]=20, + 0.5*AP(100) = 70. The 999 Base/PerLevel must not leak in.
        var thr = ExecuteEvaluator.ThresholdHp(rule, new ExecuteContext { CasterLevel = 2 }, 3000, ap: 100, bonusAd: 0, lethality: 0);
        Assert.Equal(70, thr, 3);
        // A level past the table clamps to the last entry (30), not an out-of-range throw.
        var clamped = ExecuteEvaluator.ThresholdHp(rule, new ExecuteContext { CasterLevel = 9 }, 3000, ap: 0, bonusAd: 0, lethality: 0);
        Assert.Equal(30, clamped, 3);
    }

    [Fact]
    public void PykeR_BaseByLevel_MatchesExactPiecewiseCurve_WithBonusAdAndLethalityOnTop()
    {
        // (§8) End-to-end: the real curated pyke_r rule (JSON baseByLevel + loader + evaluator).
        // BIN ByCharLevelBreakpoints: 250 flat through L6, then +40/+30/+20/+10 steps -> L6=250,
        // L9=370, L18=550. With no bonus stats the FlatHp threshold equals the per-level base.
        ExecuteEffectsDb.ResetForTests();
        var pyke = ExecuteEffectsDb.Get("pyke_r")!.Rule;

        double At(int lvl, double bonusAd = 0, double lethality = 0) => ExecuteEvaluator.ThresholdHp(
            pyke, new ExecuteContext { CasterLevel = lvl }, targetMaxHp: 3000, ap: 0, bonusAd: bonusAd, lethality: lethality);

        Assert.Equal(250, At(6), 3);   // R opens at L6, base still flat 250 (the old linear over-counted here)
        Assert.Equal(370, At(9), 3);
        Assert.Equal(550, At(18), 3);
        // +0.8 bonusAD + 1.5 lethality add on top of the per-level base: L9 370 + 0.8*100 + 1.5*20 = 480.
        Assert.Equal(370 + 0.8 * 100 + 1.5 * 20, At(9, bonusAd: 100, lethality: 20), 3);
    }

    // ── kill decision ────────────────────────────────────────────────────────────

    [Fact]
    public void Kills_StateExecute_WhenCurrentHpBelowLine_NoDamageNeeded()
    {
        // Press-R execute: no combo damage, target already under the line.
        bool kills = ExecuteEvaluator.Kills(Percent(0.05), default,
            currentHp: 90, shield: 0, mitigated: 0, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            ExecuteNegation.None);
        Assert.True(kills); // line 100, health 90
    }

    [Fact]
    public void Kills_DamageBringsBelowLine()
    {
        // Collector-style: combo leaves target below 5%.
        bool kills = ExecuteEvaluator.Kills(Percent(0.05), default,
            currentHp: 150, shield: 0, mitigated: 60, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            ExecuteNegation.None);
        Assert.True(kills); // health after = 90 <= 100
    }

    [Fact]
    public void Kills_False_WhenAboveLineAfterCombo()
    {
        bool kills = ExecuteEvaluator.Kills(Percent(0.05), default,
            currentHp: 150, shield: 0, mitigated: 0, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            ExecuteNegation.None);
        Assert.False(kills); // 150 > 100
    }

    [Fact]
    public void Kills_PiercingExecute_IgnoresShield()
    {
        bool kills = ExecuteEvaluator.Kills(Percent(0.05, pierces: true), default,
            currentHp: 90, shield: 400, mitigated: 0, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            ExecuteNegation.None);
        Assert.True(kills); // shield ignored, health 90 <= 100
    }

    [Fact]
    public void Kills_NonPiercingExecute_BlockedByRemainingShield()
    {
        bool kills = ExecuteEvaluator.Kills(Percent(0.05, pierces: false), default,
            currentHp: 90, shield: 400, mitigated: 0, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            ExecuteNegation.None);
        Assert.False(kills); // shield survives -> non-piercing execute blocked
    }

    [Theory]
    [InlineData(ExecuteNegation.Invulnerable)]
    [InlineData(ExecuteNegation.Undying)]
    [InlineData(ExecuteNegation.MinHpGuarantee)]
    public void Kills_False_WhenNegated_EvenIfPiercingAndBelowLine(ExecuteNegation negation)
    {
        bool kills = ExecuteEvaluator.Kills(Percent(0.05, pierces: true), default,
            currentHp: 10, shield: 0, mitigated: 0, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            negation);
        Assert.False(kills);
    }

    [Fact]
    public void GatedRule_InactiveBelowGate_ActiveAtGate()
    {
        var syndra = Percent(0.15, gate: 100);
        Assert.False(ExecuteEvaluator.IsActive(syndra, new ExecuteContext { Stacks = 99 }));
        Assert.True(ExecuteEvaluator.IsActive(syndra, new ExecuteContext { Stacks = 100 }));

        // Below gate: never kills even under the nominal line.
        Assert.False(ExecuteEvaluator.Kills(syndra, new ExecuteContext { Stacks = 99 },
            currentHp: 10, shield: 0, mitigated: 0, targetMaxHp: 2000, ap: 0, bonusAd: 0, lethality: 0,
            ExecuteNegation.None));
    }

    // ── curated roster (ExecuteEffectsDb) ──────────────────────────────────────────

    [Fact]
    public void Roster_LoadsKnownExecutes_AndFlagsNeedsConfirm()
    {
        ExecuteEffectsDb.ResetForTests();
        var all = ExecuteEffectsDb.All();
        Assert.NotEmpty(all);

        var ids = all.Select(e => e.Rule.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in new[] { "collector", "elder", "pyke_r", "urgot_r", "syndra_r",
                                         "draven_r", "aurelionsol_center", "zeri_p", "smolder_p", "locke_r" })
            Assert.Contains(expected, ids);

        // Every rule pierces shield (all currently-known real executes do).
        Assert.All(all, e => Assert.True(e.Rule.PiercesShield));

        // Syndra is gated at 100; Collector is a plain 5% item.
        Assert.Equal(100, ExecuteEffectsDb.Get("syndra_r")!.Rule.GateStacks);
        Assert.Equal(ExecuteSource.Item, ExecuteEffectsDb.Get("collector")!.Rule.Source);
        Assert.Equal(ExecuteSource.Buff, ExecuteEffectsDb.Get("elder")!.Rule.Source);

        // (loop 104) The two formerly-approximate rows are now BIN-confirmed and no longer flagged:
        // Pyke R's exact piecewise curve is modeled via baseByLevel; Smolder P's 6.5% is fixed. The
        // whole seed roster is now confirmed.
        Assert.False(ExecuteEffectsDb.Get("pyke_r")!.NeedsConfirm);
        Assert.False(ExecuteEffectsDb.Get("smolder_p")!.NeedsConfirm);
        Assert.DoesNotContain(all, e => e.NeedsConfirm);
    }

    [Fact]
    public void Roster_ExcludesNonExecutes_GarenMelChogath()
    {
        ExecuteEffectsDb.ResetForTests();
        var ids = ExecuteEffectsDb.All().Select(e => e.Rule.Id.ToLowerInvariant()).ToList();
        // These resemble executes but are damage/scaling, not kill-lines (M24 v1.1/v1.2).
        Assert.DoesNotContain(ids, id => id.Contains("garen"));
        Assert.DoesNotContain(ids, id => id.Contains("mel"));
        Assert.DoesNotContain(ids, id => id.Contains("chogath") || id.Contains("cho_"));
    }

    // ── KillableCalculator integration (back-compat is the headline assertion) ─────

    private const string TargetDataJson = """
    {
      "champions": {
        "Attacker": { "baseHealth": 500, "healthPerLevel": 0, "baseArmor": 0, "armorPerLevel": 0,
          "baseMagicResist": 0, "magicResistPerLevel": 0, "baseAttackDamage": 0, "attackDamagePerLevel": 0,
          "abilities": [] },
        "Target": { "baseHealth": 2000, "healthPerLevel": 0, "baseArmor": 0, "armorPerLevel": 0,
          "baseMagicResist": 0, "magicResistPerLevel": 0, "baseAttackDamage": 0, "attackDamagePerLevel": 0,
          "abilities": [] },
        "_default": { "baseHealth": 500, "healthPerLevel": 0, "baseArmor": 0, "armorPerLevel": 0,
          "baseMagicResist": 0, "magicResistPerLevel": 0, "baseAttackDamage": 0, "attackDamagePerLevel": 0,
          "abilities": [] }
      },
      "items": {}
    }
    """;

    private static KillableCalculator Calc() => new(StaticGameDataLoader.LoadFromJson(TargetDataJson));

    [Fact]
    public void Evaluate_NoExecuteRules_IdenticalToDamageOnly_LowHpTargetNotKillable()
    {
        var calc = Calc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 90 };

        var result = calc.Evaluate(in atk, in tgt); // no rules

        Assert.False(result.Killable);          // 0 damage < 90 HP
        Assert.False(result.KillableByExecute);
        Assert.Equal(0, result.ExecuteThresholdHp);
        Assert.Null(result.ExecuteRuleId);
    }

    [Fact]
    public void Evaluate_WithExecuteRule_LowHpTargetKillable_EvenWithZeroDamage()
    {
        var calc = Calc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 90 };
        var rules = new[] { Percent(0.05) }; // 5% of 2000 = 100 HP line; target at 90

        var result = calc.Evaluate(in atk, in tgt, rules);

        Assert.True(result.Killable);
        Assert.True(result.KillableByExecute);
        Assert.Equal(100, result.ExecuteThresholdHp);
    }

    [Fact]
    public void Evaluate_ExecuteNegated_NotKillable()
    {
        var calc = Calc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 90, Negation = ExecuteNegation.Invulnerable };
        var rules = new[] { Percent(0.05) };

        var result = calc.Evaluate(in atk, in tgt, rules);

        Assert.False(result.Killable);
        Assert.False(result.KillableByExecute);
        Assert.Equal(100, result.ExecuteThresholdHp); // line still reported for the overlay
    }
}
