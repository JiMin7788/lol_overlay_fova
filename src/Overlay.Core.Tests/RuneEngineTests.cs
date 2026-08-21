using Overlay.Core.ChampionDb;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M06 Rune Engine (docs/modules/M06_RUNE_ENGINE.md):
///  - auto/manual classification driven generically by RuneData.ApiTrackable (not a
///    hardcoded rune-name list) — verified against the real 8 non-trackable rune ids
///    from RuneApiTrackability plus a synthetic trackable rune.
///  - a manual (apiTrackable:false) rune starts inactive and only becomes active via
///    an explicit SetManualFlag(..., true) call.
///  - an auto-tracked rune's approximate stack responds to a real published GAME.*
///    event (GAME.CHAMPION_DIED), driven through the real EventBus.
///  - a merged auto+manual scenario.
///  - the Reviewer Checklist proof: no code path silently activates a non-trackable rune.
///
/// RuneRepository and EventBus are both static/process-wide, so every test resets both
/// first (see RuneRepositoryTests-style isolation already used for EventBus in
/// EventBusTests). AssemblyInfo.cs already disables cross-class test parallelization.
/// </summary>
public class RuneEngineTests
{
    // Real ids from RuneApiTrackability (spec's own worked manual-group examples).
    private const string CheapShotId = "8126";   // 비열한 한방 — apiTrackable:false
    private const string SuddenImpactId = "8143"; // 돌발일격 — apiTrackable:false
    private const string FirstStrikeId = "8369";  // 선제공격 — apiTrackable:false

    // Synthetic auto-trackable rune (Conqueror stand-in) — M11's actual Conqueror id
    // isn't listed anywhere in the M06/M11/M01 material available to this module, so a
    // synthetic id keeps the test independent of an unpublished real id while still
    // exercising the same ApiTrackable:true code path.
    private const string ConquerorId = "9999";

    public RuneEngineTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = ConquerorId, Name = "Conqueror", Tree = "Precision", EffectFormula = "stack-based", ApiTrackable = true },
            new RuneData { Id = CheapShotId, Name = "Cheap Shot", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
            new RuneData { Id = SuddenImpactId, Name = "Sudden Impact", Tree = "Domination", EffectFormula = null, ApiTrackable = false },
            new RuneData { Id = FirstStrikeId, Name = "First Strike", Tree = "Inspiration", EffectFormula = null, ApiTrackable = false },
        });
    }

    [Fact]
    public void Classification_IsDrivenByApiTrackable_NotHardcodedNames()
    {
        var engine = new RuneEngine();
        var config = new UserRuneConfig(new[] { ConquerorId, CheapShotId });

        var effects = engine.GetActiveEffects("Aatrox", config);

        var conqueror = Assert.Single(effects, e => e.RuneId == ConquerorId);
        Assert.True(conqueror.IsApiTrackable);
        Assert.Null(conqueror.IsManuallyActive);
        Assert.NotNull(conqueror.CurrentStack);

        var cheapShot = Assert.Single(effects, e => e.RuneId == CheapShotId);
        Assert.False(cheapShot.IsApiTrackable);
        Assert.Null(cheapShot.CurrentStack);
        Assert.NotNull(cheapShot.IsManuallyActive);
    }

    [Fact]
    public void ManualRune_StartsInactive_ThenReflectsExplicitSetManualFlag()
    {
        var engine = new RuneEngine();
        var config = new UserRuneConfig(new[] { SuddenImpactId });

        var before = engine.GetActiveEffects("Zed", config).Single();
        Assert.False(before.IsManuallyActive); // never silently active

        engine.SetManualFlag(SuddenImpactId, true);
        var after = engine.GetActiveEffects("Zed", config).Single();
        Assert.True(after.IsManuallyActive);

        engine.SetManualFlag(SuddenImpactId, false);
        var reset = engine.GetActiveEffects("Zed", config).Single();
        Assert.False(reset.IsManuallyActive);
    }

    [Fact]
    public void AutoRune_StackIncrements_OnRealPublishedChampionDiedEvent_WhereTrackedChampionIsKiller()
    {
        var engine = new RuneEngine();
        var config = new UserRuneConfig(new[] { ConquerorId });

        var initial = engine.GetActiveEffects("Aatrox", config).Single();
        Assert.Equal(0, initial.CurrentStack);

        // Real GAME.CHAMPION_DIED publish through the real EventBus, naming the
        // tracked champion ("Aatrox") as the killer — the heuristic's "recent combat
        // participation" signal.
        EventBus.EventBus.Publish(
            "GAME.CHAMPION_DIED",
            new ChampionDiedPayload(ChampionName: "Ashe", KillerName: "Aatrox", Timestamp: 120.0, RespawnTimer: 0.0),
            "TestSource");

        var after = engine.GetActiveEffects("Aatrox", config).Single();
        Assert.Equal(1, after.CurrentStack);
    }

    [Fact]
    public void AutoRune_StackResets_WhenTrackedChampionIsTheVictim()
    {
        var engine = new RuneEngine();
        var config = new UserRuneConfig(new[] { ConquerorId });

        engine.UpdateStack(ConquerorId, 3);
        Assert.Equal(3, engine.GetActiveEffects("Aatrox", config).Single().CurrentStack);

        EventBus.EventBus.Publish(
            "GAME.CHAMPION_DIED",
            new ChampionDiedPayload(ChampionName: "Aatrox", KillerName: "Ashe", Timestamp: 130.0, RespawnTimer: 0.0),
            "TestSource");

        Assert.Equal(0, engine.GetActiveEffects("Aatrox", config).Single().CurrentStack);
    }

    [Fact]
    public void GetActiveEffects_MergesAutoAndManualGroups_IntoOneArray()
    {
        var engine = new RuneEngine();
        engine.SetManualFlag(FirstStrikeId, true);
        engine.UpdateStack(ConquerorId, 2);

        var config = new UserRuneConfig(new[] { ConquerorId, CheapShotId, FirstStrikeId });
        var effects = engine.GetActiveEffects("Zed", config);

        Assert.Equal(3, effects.Length);

        var conqueror = effects.Single(e => e.RuneId == ConquerorId);
        Assert.True(conqueror.IsApiTrackable);
        Assert.Equal(2, conqueror.CurrentStack);

        var cheapShot = effects.Single(e => e.RuneId == CheapShotId);
        Assert.False(cheapShot.IsApiTrackable);
        Assert.False(cheapShot.IsManuallyActive); // never toggled -> inactive

        var firstStrike = effects.Single(e => e.RuneId == FirstStrikeId);
        Assert.False(firstStrike.IsApiTrackable);
        Assert.True(firstStrike.IsManuallyActive); // explicitly toggled
    }

    /// <summary>
    /// Reviewer Checklist proof: "apiTrackable:false 룬이 자동으로 활성 처리되는 코드 경로가
    /// 없는가?" — sweeps every one of RuneApiTrackability's real 8 non-trackable rune ids
    /// (not just the 3 named in Acceptance Criteria) through GetActiveEffects with no
    /// SetManualFlag call at all, and asserts every single one reads back inactive.
    /// </summary>
    [Fact]
    public void ReviewerChecklist_NoNonTrackableRuneIsEverAutoActivated()
    {
        var allNonTrackable = RuneApiTrackability.NonTrackableRuneIds
            .Select(id => new RuneData
            {
                Id = id.ToString(),
                Name = $"Rune{id}",
                Tree = "Test",
                EffectFormula = null,
                ApiTrackable = false,
            })
            .ToArray();

        RuneRepository.ResetForTests();
        RuneRepository.Initialize(allNonTrackable);

        var engine = new RuneEngine();
        var config = new UserRuneConfig(allNonTrackable.Select(r => r.Id).ToArray());

        // No SetManualFlag call anywhere for any of these 8 rune ids.
        var effects = engine.GetActiveEffects("Aatrox", config);

        Assert.Equal(8, effects.Length);
        Assert.All(effects, e =>
        {
            Assert.False(e.IsApiTrackable);
            Assert.False(e.IsManuallyActive);
        });
    }

    [Fact]
    public void UnknownRuneId_IsSkipped_NotFabricatedAsActive()
    {
        var engine = new RuneEngine();
        var config = new UserRuneConfig(new[] { "not-a-real-rune-id" });

        var effects = engine.GetActiveEffects("Aatrox", config);

        Assert.Empty(effects);
    }

    // ---------------------------------------------------------------- RuneEffectDb-backed DamageBonus

    /// <summary>
    /// Hand-verified: Cheap Shot (8126) is a level-scaled TRUE-damage flat rune with no AD/AP ratio
    /// (rune_effects.json: baseAtLevel1=10, baseAtLevel18=49.12 — cited from the live wiki infobox,
    /// see the file's own "_note"). At level 11 the linear interpolation is
    /// 10 + (49.12-10)/17*(11-1) = 10 + 2.3011764705882353*10 = 33.011764705882353.
    /// </summary>
    [Fact]
    public void ManualRune_WithRealFormula_AndActiveFlag_AndCasterStats_ProducesRealDamageBonus()
    {
        var engine = new RuneEngine();
        engine.SetManualFlag(CheapShotId, true);
        var config = new UserRuneConfig(new[] { CheapShotId });
        var casterStats = new RuneCasterStats(Level: 11, BonusAd: 40, Ap: 30, MaxHealth: 2000, IsMelee: true);

        var effect = engine.GetActiveEffects("Aatrox", config, casterStats).Single();

        Assert.True(effect.IsManuallyActive);
        Assert.NotNull(effect.DamageBonus);
        Assert.Equal(33.011764705882353, effect.DamageBonus!.Value, 6);
        Assert.Equal(RuneDamageType.TRUE, effect.DamageType);
    }

    /// <summary>
    /// Regression guard for the Policy Compliance invariant: even when real caster stats AND a
    /// covered formula are both available, a manual rune's DamageBonus stays null/inert unless the
    /// user explicitly toggled its checkbox on (spec Policy Compliance Checklist item 1 — "API로
    /// 확인 불가능한 룬 상태를 임의로 활성으로 가정하여 표시하지 않는다"). Proves the new
    /// RuneEffectDb wiring did not weaken the pre-existing "unset manual flag == inactive" guarantee
    /// <see cref="ManualRune_StartsInactive_ThenReflectsExplicitSetManualFlag"/> already covers.
    /// </summary>
    [Fact]
    public void ManualRune_WithRealFormula_ButFlagOff_StaysInert_EvenWithCasterStatsSupplied()
    {
        var engine = new RuneEngine();
        var config = new UserRuneConfig(new[] { CheapShotId }); // SetManualFlag never called
        var casterStats = new RuneCasterStats(Level: 18, BonusAd: 999, Ap: 999, MaxHealth: 9999, IsMelee: true);

        var effect = engine.GetActiveEffects("Aatrox", config, casterStats).Single();

        Assert.False(effect.IsManuallyActive);
        Assert.Null(effect.DamageBonus);
        Assert.Null(effect.DamageType);
    }

    /// <summary>
    /// Hand-verified: Grasp of the Undying (8437) is a pure %-max-health MAGIC rune (no flat base,
    /// no AD/AP ratio) — meleeMaxHealthPercent=0.035 per rune_effects.json's cited wiki quote.
    /// 0.035 * 2000 = 70.
    /// </summary>
    [Fact]
    public void ManualRune_MaxHealthPercentFormula_ScalesByMeleeVsRangedCasterFlag()
    {
        var graspId = "8437";
        RuneRepository.ResetForTests();
        RuneRepository.Initialize(new[]
        {
            new RuneData { Id = graspId, Name = "Grasp of the Undying", Tree = "Resolve", EffectFormula = null, ApiTrackable = false },
        });

        var engine = new RuneEngine();
        engine.SetManualFlag(graspId, true);
        var config = new UserRuneConfig(new[] { graspId });
        var meleeStats = new RuneCasterStats(Level: 11, BonusAd: 0, Ap: 0, MaxHealth: 2000, IsMelee: true);

        var effect = engine.GetActiveEffects("Aatrox", config, meleeStats).Single();

        Assert.Equal(70.0, effect.DamageBonus!.Value, 6);
        Assert.Equal(RuneDamageType.MAGIC, effect.DamageType);
    }

    /// <summary>
    /// First Strike (8369) is deliberately absent from rune_effects.json (its wiki-documented
    /// effect is a % damage AMPLIFIER on other hits, not a standalone flat/ratio damage source —
    /// see rune_effects.json's top-level "_note"). Even fully active with real caster stats, its
    /// DamageBonus must stay null rather than a forced/incorrect number.
    /// </summary>
    [Fact]
    public void ManualRune_WithNoCoveredFormula_StaysNull_EvenWhenActive()
    {
        var engine = new RuneEngine();
        engine.SetManualFlag(FirstStrikeId, true);
        var config = new UserRuneConfig(new[] { FirstStrikeId });
        var casterStats = new RuneCasterStats(Level: 11, BonusAd: 40, Ap: 30, MaxHealth: 2000, IsMelee: true);

        var effect = engine.GetActiveEffects("Zed", config, casterStats).Single();

        Assert.True(effect.IsManuallyActive);
        Assert.Null(effect.DamageBonus);
        Assert.Null(effect.DamageType);
    }
}
