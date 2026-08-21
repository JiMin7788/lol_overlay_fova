namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for <see cref="KillableCalculator"/> (DA-001) — the one-combo
/// KILLABLE burst calculator. Verifies the exact-lethal-threshold boundary (mitigated
/// combo damage EXACTLY equal to effective HP counts as killable), live-stat overrides,
/// shield folding, the default-profile fallback flags, the Zed-R-style amplifier pass,
/// and lethality-to-flat-armor-pen conversion — all hand-computed against the formulas
/// documented in the class itself.
/// </summary>
public class KillableCalculatorTests
{
    // A single physical "Q" that deals 1.0 * totalAD, no amplifier — the simplest
    // combo shape, used by most tests below.
    private const string BasicDataJson = """
    {
      "champions": {
        "Attacker": {
          "baseHealth": 500, "healthPerLevel": 0,
          "baseArmor": 0, "armorPerLevel": 0,
          "baseMagicResist": 0, "magicResistPerLevel": 0,
          "baseAttackDamage": 50, "attackDamagePerLevel": 0,
          "abilities": [
            { "name": "Q", "type": "Physical", "flat": 0, "perAd": 1, "perBonusAd": 0, "perAp": 0, "percentOfComboDamage": 0 }
          ]
        },
        "Target": {
          "baseHealth": 500, "healthPerLevel": 0,
          "baseArmor": 0, "armorPerLevel": 0,
          "baseMagicResist": 0, "magicResistPerLevel": 0,
          "baseAttackDamage": 0, "attackDamagePerLevel": 0,
          "abilities": []
        },
        "_default": {
          "baseHealth": 500, "healthPerLevel": 0,
          "baseArmor": 30, "armorPerLevel": 0,
          "baseMagicResist": 30, "magicResistPerLevel": 0,
          "baseAttackDamage": 50, "attackDamagePerLevel": 0,
          "abilities": []
        }
      },
      "items": {
        "9001": { "name": "TestLethalityItem", "lethality": 18 }
      }
    }
    """;

    private static KillableCalculator BasicCalc() => new(StaticGameDataLoader.LoadFromJson(BasicDataJson));

    // ── Exact-lethal-threshold boundary ─────────────────────────────────────────

    [Fact]
    public void Evaluate_DamageExactlyEqualsEffectiveHp_ReportsKillableTrue()
    {
        var calc = BasicCalc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 50 }; // == combo damage

        var result = calc.Evaluate(in atk, in tgt);

        Assert.True(result.Killable);
        Assert.Equal(50, result.TotalDamage);
        Assert.Equal(0, result.HpDelta);
        Assert.Equal(0, result.TargetHpRemaining);
    }

    [Fact]
    public void Evaluate_DamageOneBelowEffectiveHp_ReportsKillableFalse()
    {
        var calc = BasicCalc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 51 }; // 1 HP more than combo

        var result = calc.Evaluate(in atk, in tgt);

        Assert.False(result.Killable);
        Assert.Equal(-1, result.HpDelta);
        Assert.Equal(1, result.TargetHpRemaining);
    }

    // ── Shield folding (SKILL: subtract shield before compare) ────────────────

    [Fact]
    public void Evaluate_ActiveShield_IsFoldedIntoEffectiveHp_BeforeComparison()
    {
        var calc = BasicCalc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 40, ActiveShield = 20 };

        var result = calc.Evaluate(in atk, in tgt);

        Assert.Equal(60, result.TargetEffectiveHp); // 40 hp + 20 shield
        Assert.False(result.Killable); // 50 dmg < 60 effective hp
        Assert.Equal(10, result.TargetHpRemaining);
    }

    // ── Live attacker stats (bonus-AD ratio) ───────────────────────────────────

    [Fact]
    public void Evaluate_LiveAttackDamage_OverridesStaticBase_AndFeedsBonusAdRatio()
    {
        const string json = """
        {
          "champions": {
            "Attacker2": {
              "baseHealth": 500, "healthPerLevel": 0,
              "baseArmor": 0, "armorPerLevel": 0,
              "baseMagicResist": 0, "magicResistPerLevel": 0,
              "baseAttackDamage": 50, "attackDamagePerLevel": 0,
              "abilities": [
                { "name": "Q", "type": "Physical", "flat": 0, "perAd": 0, "perBonusAd": 1, "perAp": 0, "percentOfComboDamage": 0 }
              ]
            },
            "Target": {
              "baseHealth": 500, "healthPerLevel": 0,
              "baseArmor": 0, "armorPerLevel": 0,
              "baseMagicResist": 0, "magicResistPerLevel": 0,
              "baseAttackDamage": 0, "attackDamagePerLevel": 0,
              "abilities": []
            }
          },
          "items": {}
        }
        """;
        var calc = new KillableCalculator(StaticGameDataLoader.LoadFromJson(json));
        // Base AD = 50 (from static), live total AD = 200 => bonus AD = 150.
        var atk = new AttackerInput { ChampionName = "Attacker2", Level = 1, LiveAttackDamage = 200 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 150 };

        var result = calc.Evaluate(in atk, in tgt);

        Assert.Equal(150, result.TotalDamage); // 1.0 * bonusAD(150), no base-AD ratio on this ability
        Assert.True(result.Killable);
    }

    // ── Default-profile fallback flags ─────────────────────────────────────────

    [Fact]
    public void Evaluate_UnknownChampions_SetsUsedDefaultProfileFlags()
    {
        var calc = BasicCalc();
        var atk = new AttackerInput { ChampionName = "NotInDataFile", Level = 1 };
        var tgt = new TargetInput { ChampionName = "AlsoNotInDataFile", Level = 1, CurrentHealth = 100 };

        var result = calc.Evaluate(in atk, in tgt);

        Assert.True(result.UsedDefaultAttackerProfile);
        Assert.True(result.UsedDefaultTargetProfile);
    }

    [Fact]
    public void Evaluate_KnownChampions_DoesNotSetDefaultProfileFlags()
    {
        var calc = BasicCalc();
        var atk = new AttackerInput { ChampionName = "Attacker", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 100 };

        var result = calc.Evaluate(in atk, in tgt);

        Assert.False(result.UsedDefaultAttackerProfile);
        Assert.False(result.UsedDefaultTargetProfile);
    }

    // ── Amplifier pass (Zed-R-style: percent of the BASE combo, not compounding) ──

    [Fact]
    public void Evaluate_AmplifierAbility_ScalesBaseComboDamage_ByItsOwnFraction()
    {
        const string json = """
        {
          "champions": {
            "Amp": {
              "baseHealth": 500, "healthPerLevel": 0,
              "baseArmor": 0, "armorPerLevel": 0,
              "baseMagicResist": 0, "magicResistPerLevel": 0,
              "baseAttackDamage": 100, "attackDamagePerLevel": 0,
              "abilities": [
                { "name": "Q", "type": "Physical", "flat": 0, "perAd": 1, "perBonusAd": 0, "perAp": 0, "percentOfComboDamage": 0 },
                { "name": "R", "type": "Physical", "flat": 0, "perAd": 0, "perBonusAd": 0, "perAp": 0, "percentOfComboDamage": 0.5 }
              ]
            },
            "Target": {
              "baseHealth": 500, "healthPerLevel": 0,
              "baseArmor": 0, "armorPerLevel": 0,
              "baseMagicResist": 0, "magicResistPerLevel": 0,
              "baseAttackDamage": 0, "attackDamagePerLevel": 0,
              "abilities": []
            }
          },
          "items": {}
        }
        """;
        var calc = new KillableCalculator(StaticGameDataLoader.LoadFromJson(json));
        var atk = new AttackerInput { ChampionName = "Amp", Level = 1 };
        var tgt = new TargetInput { ChampionName = "Target", Level = 1, CurrentHealth = 150 }; // 100 base * 1.5

        var result = calc.Evaluate(in atk, in tgt);

        Assert.Equal(150, result.TotalDamage);
        Assert.True(result.Killable);
    }

    // ── Lethality -> flat armor pen conversion ─────────────────────────────────

    [Fact]
    public void Evaluate_LethalityItem_ReducesEffectiveArmor_ByLevelScaledFlatPen()
    {
        var calc = BasicCalc();
        // flatPen = 18 * (0.6 + 0.4 * 1/18) = 10.8 + 0.4 = 11.2
        var atk = new AttackerInput
        {
            ChampionName = "Attacker",
            Level = 1,
            ItemIds = new[] { 9001 },
            ItemCount = 1,
        };
        var tgt = new TargetInput { ChampionName = "_default", Level = 1, CurrentHealth = 1000 };
        // "_default" isn't looked up via GetChampionOrDefault's fallback path here (target
        // champion name IS "_default", which the dictionary happens to contain), so its
        // 30 base armor is used directly: effectiveArmor = 30 - 11.2 = 18.8.

        var result = calc.Evaluate(in atk, in tgt);

        Assert.Equal(18.8, result.EffectiveArmor, precision: 3);
        // mitigated = 50 * 100/118.8 = 42.0876 (approx)
        Assert.Equal(42.09, result.TotalDamage, precision: 2);
    }

    // ── ResistMultiplier: positive resist mitigates, negative resist amplifies ──

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(100.0, 0.5)]
    [InlineData(-50.0, 1.3333333333333333)]
    public void ResistMultiplier_MatchesLeagueFormula(double effectiveResist, double expected)
    {
        Assert.Equal(expected, KillableCalculator.ResistMultiplier(effectiveResist), precision: 6);
    }

    // ── GameSnapshot/ScoreboardEntry convenience overload ──────────────────────

    [Fact]
    public void Evaluate_FromSnapshotAndScoreboardEntry_UsesLiveActivePlayerStats()
    {
        var calc = BasicCalc();
        var snapshot = new GameSnapshot
        {
            HasData = true,
            Level = 1,
            Stats = new ActivePlayerStats { AttackDamage = 50, AbilityPower = 0 },
        };
        var target = new ScoreboardEntry { ChampionName = "Target", Level = 1 };

        var result = calc.Evaluate(snapshot, "Attacker", target);

        // Target current HP unknown via this overload -> assumes full HP (500, from static data).
        Assert.Equal(500, result.TargetCurrentHp);
        Assert.Equal(50, result.TotalDamage);
        Assert.False(result.Killable);
    }
}
