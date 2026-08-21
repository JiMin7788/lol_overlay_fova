using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode; // avoid CS0104

namespace Overlay.Core.Tests;

/// <summary>
/// GOLDEN #6 — Maokai, the mStat-12 TARGET-basis pole (paired with Tam Kench's caster pole) for the
/// §17 audit. Measured 2026-07-14 (target L1 Kennen, MR 30):
///
/// Q (Bramble Smash) = BaseDamage + 40%×AP + **rank-fixed % of the TARGET's max health** (r1 = 2%,
/// NO AP term). Magic. Verified: Kennen 580 HP → 69, Kennen 1890 HP → 89 (Maokai's own HP fixed;
/// only the TARGET's HP changed and Q rose ⇒ TARGET-basis, tooltip "적최대체력"):
///   75 + 0.40×9 + 0.02×580  = 90.2  → ×100/130 = 69.4 ≈ 69
///   75 + 0.40×9 + 0.02×1890 = 116.4 → ×100/130 = 89.5 ≈ 89
///
/// E (Sapling Toss) = base + AP + **5% of Maokai's OWN bonus health** (mStat 12, formula 2 = caster
/// bonus). Enhanced (bush) = EmpoweredBase + 50%×AP + **10% own bonus health**. Measured enhanced E:
/// Maokai L1 (rune +10 bonus HP), AP 9 → tooltip 106, live 81 (×100/130). This is CASTER-basis and
/// the engine already supports it (attacker-side stat resolver).
///
/// KEY (corrects an earlier Cowork note): Q's target-max term is a rank-fixed %maxHP, so it needs NO
/// new "target-side resolver" engine feature — it is a plain DEFENDER %maxHP hit (hpBasis Max), the
/// same supported mechanism as K'Sante W and Varus blight. Q is currently curated as a single
/// `{calc: TotalDamage}` whose embedded mStat-12 wrongly resolves to the CASTER, so the engine
/// under/mis-counts Q vs a target. FIX = split Q into two hits (base+AP calc + hpPercentCalc /
/// hpBasis Max, rank %), exactly like the K'Sante W split.
///
/// Cowork-authored (no dotnet SDK) — UNVERIFIED until Claude Code builds+runs.
/// </summary>
public class GoldenMaokaiTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public GoldenMaokaiTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "GoldenMaokaiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static void AssertWithinOne(double expected, double actual, string what)
        => Assert.True(Math.Abs(actual - expected) <= 1.0,
            $"{what}: expected {expected} ±1, engine gave {actual:0.##} (Δ={actual - expected:0.##})");

    private static ChampionData Maokai()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", "maokai.bin.json"));
        var binSkills = ChampionBinParser.ParseChampion("Maokai", json);
        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
            skills[slot] = new SkillData
            {
                Key = slot, Name = "Maokai" + slot,
                DataValues = bin.DataValues, SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts, MaxRank = slot == "R" ? 3 : 5,
            };
        return new ChampionData
        {
            Id = "Maokai", Name = "Maokai", Skills = skills,
            BaseStats = new ChampionBaseStats(), StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ChampionData Dummy(string id, double hp, double armor, double mr) => new()
    {
        Id = id, Name = id,
        BaseStats = new ChampionBaseStats { Hp = hp, Armor = armor, Mr = mr },
        StatsPerLevel = new ChampionStatsPerLevel(),
    };

    private static ActivePlayerStats Stats(double ap, double maxHp) => new()
    {
        AttackDamage = 0, AbilityPower = ap, MaxHealth = maxHp,
        AbilityQ = 1, AbilityW = 1, AbilityE = 1, AbilityR = 1,
    };

    private static GameSnapshot Snapshot(ActivePlayerStats stats)
    {
        var snap = new GameSnapshot { HasData = true, ActivePlayerSummonerName = "Me", Level = 1, PlayerCount = 2, Stats = stats };
        snap.Players[0].SummonerName = "Me"; snap.Players[0].ChampionName = "Maokai";
        snap.Players[0].Team = "ORDER"; snap.Players[0].Level = 1;
        snap.Players[1].SummonerName = "Target"; snap.Players[1].ChampionName = "Target";
        snap.Players[1].Team = "CHAOS"; snap.Players[1].Level = 1;
        return snap;
    }

    private ComboResult RunSlot(GameSnapshot snap, string slot)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);
        var draft = editor.CreateCombo("Maokai", "c");
        editor.AddNode(draft.Id, new ComboNode(
            Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot, Cooldown: 0, Mana: 0, Damage: 0,
            DamageType: ComboDamageType.Magic, RatioAD: 0, RatioBonusAD: 0, RatioAP: 0,
            CastTime: 0, Delay: 0, TravelTime: 0));
        editor.SaveCombo(draft.Id);
        using var runner = new ComboRunner(engine, config, () => snap);
        runner.Start();
        ComboResult? received = null;
        using var gate = new ManualResetEventSlim(false);
        EventBus.EventBus.Subscribe("UI.COMBO_RESULT", evt => { received = (evt.Payload as ComboHudResult)?.Result; gate.Set(); });
        EventBus.EventBus.Publish("COMBO.TRIGGER", draft.Id, "M13");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(2)), "UI.COMBO_RESULT was not delivered");
        Assert.NotNull(received);
        return received!;
    }

    // ── E enhanced: caster (own) bonus-health basis — engine supports this now ────────
    [Fact]
    public void Golden_Maokai_EnhancedE_CasterBonusHealth_Is106()
    {
        var maokai = Maokai();
        // TotalEmpoweredDamage = EmpoweredBase + 50%×AP + 10%×own bonus health. Maokai L1, AP 9,
        // bonus HP 10 (rune) → raw ≈ 106 (live 81 = ×100/130 vs Kennen MR 30). BIN-referenced.
        var stats = Stats(ap: 9, maxHp: 10); // BaseStats.Hp = 0 ⇒ bonus health = 10
        double raw = SkillDamage.ComputeCalcDamage(maokai, "E", "TotalEmpoweredDamage", stats, level: 1)!.Value;
        AssertWithinOne(106, raw, "Maokai enhanced E raw (base + 50%AP + 10% own bonus HP)");
    }

    // ── Q target-max-health basis — Q split LANDED (Cowork 2026-07-16). Maokai.json Q is two hits:
    // hit1 {calc: TotalDamage} = base + APRatio×AP (no health term in that calc), and hit2
    // {hpPercentDataValue: "BasePercentHealth", hpBasis: Max} = rank-% of the TARGET's max health
    // (r1 = 2%).
    //
    // PATCH 16.15 REWRITE (2026-08-11, CLAUDE_CODE_TODO §73). The original form asserted the two
    // recorded absolutes (580→69, 1890→89) recorded at AP 9 on patch 16.14. 16.15 raised
    // MaokaiQ:APRatio 0.40→0.50 (user-confirmed off the in-client Shift tooltip: "75 (+50% 주문력)
    // + 최대 체력의 2%"), which moves both absolutes by 0.1×9×(100/130) = 0.69 — so those two
    // constants are now patch-stale, and they were NOT re-measured.
    //
    // They are also, in hindsight, the WRONG thing to have asserted. At AP 9 the gap between the
    // 0.40 and 0.50 hypotheses is 0.69 damage while this file's tolerance is ±1 — the row could
    // never have discriminated the ratio it appeared to pin, and swapping to 0.50 breaks it by only
    // 0.08 / 0.23, i.e. inside the rounding noise of an integer-displayed in-game number.
    //
    // What genuinely survives is the DIFFERENCE between the two casts. Maokai's own stats, the base
    // damage and the whole AP term are identical across them, so they cancel exactly, leaving only
    // the target-max-health term — which is precisely what golden #6 exists to prove (TARGET-basis,
    // not caster) and is invariant to every scalar this patch moved. Measured 89−69 = 20; model
    // 0.02×(1890−580)×100/130 = 20.154. A caster-basis reading would give 0.
    [Fact]
    public void Golden_Maokai_Q_TargetMaxHealth_MeasuredDifferential_ProvesTargetBasis()
    {
        double Cast(double targetHp)
        {
            // Reset between the two casts so the second run cannot inherit the first's EventBus
            // subscriptions; Initialize replaces the champion dictionary wholesale, so re-calling
            // it is the supported way to swap the target (see ChampionRepository.Initialize).
            EventBus.EventBus.ResetForTests();
            ChampionRepository.Initialize(new[] { Maokai(), Dummy("Target", targetHp, 30, 30) });
            return RunSlot(Snapshot(Stats(ap: 9, maxHp: 0)), "Q").TotalDamage;
        }

        double low = Cast(580), high = Cast(1890);

        AssertWithinOne(20, high - low, "Maokai Q differential as target maxHP goes 580 -> 1890");
    }
}
