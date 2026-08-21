using Overlay.Core;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Config;
using Overlay.Core.Damage;
using Overlay.Core.EventBus;
using Overlay.Core.Runes;
using ComboNode = Overlay.Core.Combo.ComboNode;

namespace Overlay.Core.Tests;

/// <summary>
/// Regression for the §10 damage bug (user report: Locke, AD 73 / AP 18 / Lv 7, a combo of a
/// SINGLE plain auto-attack showed 243 instead of ~92). Root cause: ComboRunner gathered on-hit
/// bonus effects from EVERY source slot ({P,Q,W,E,R}) and applied them to every auto-attack, so an
/// ABILITY-slot on-hit — an EMPOWERED next-attack that only fires after its ability is cast (e.g.
/// Locke's Q "Soul Nail", Viktor's Q "Discharge") — was wrongly added to plain autos that never
/// followed that ability.
///
/// The fix: a P-slot on-hit is an always-on passive (every AA); a Q/W/E/R-slot on-hit fires only on
/// an AA that FOLLOWS its ability cast in the sequence (tracked by ComboRunner's castSlots). These
/// tests pin that gate down against REAL curated data (Locke.json / Viktor.json) + live BIN numbers.
///
/// Design: the empowered hit's value is proven by the DIFFERENCE between two combos that share the
/// exact same ability + auto but differ only in ORDER — [Q, AA] (auto follows Q → empowered) minus
/// [AA, Q] (auto precedes Q → not empowered) — so the ability's own direct damage cancels and only
/// the empowered on-hit remains. No enemy is on board, so FallbackDefender applies zero resistance
/// and each hit contributes its raw computed value to the total (same convention as
/// ComboDamageModelTests.AhriQ_MultiHit).
/// </summary>
public class AbilitySlotOnHitGatingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public AbilitySlotOnHitGatingTests()
    {
        EventBus.EventBus.ResetForTests();
        RuneRepository.ResetForTests();
        ChampionRepository.ResetForTests();
        ChampionSummary.ResetForTests();
        SkillDamageDb.ResetForTests();

        _dir = Path.Combine(Path.GetTempPath(), "AbilitySlotOnHitGatingTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "user_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // ── fixtures (mirrors ComboDamageModelTests: real BIN calcs from the copied test data) ──

    private static string FixturePath(string champion)
        => Path.Combine(AppContext.BaseDirectory, "data", "communitydragon", $"{champion}.bin.json");

    private static ChampionData LoadChampionFromBin(string championId)
    {
        var json = File.ReadAllText(FixturePath(championId.ToLowerInvariant()));
        var binSkills = ChampionBinParser.ParseChampion(championId, json);

        var skills = new Dictionary<string, SkillData>();
        foreach (var (slot, bin) in binSkills)
        {
            skills[slot] = new SkillData
            {
                Key = slot,
                Name = championId + slot,
                DataValues = bin.DataValues,
                SpellCalculations = bin.SpellCalculations,
                MaxRank = slot == "R" ? 3 : 5,
            };
        }

        return new ChampionData
        {
            Id = championId,
            Name = championId,
            Skills = skills,
            BaseStats = new ChampionBaseStats(),
            StatsPerLevel = new ChampionStatsPerLevel(),
        };
    }

    private static ComboNode SkillNode(string slot) => new(
        Id: $"{slot}_0", NodeType: ComboNodeType.Skill, Name: slot,
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Magic,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    private static ComboNode AaNode() => new(
        Id: "AA_0", NodeType: ComboNodeType.Aa, Name: "AA",
        Cooldown: 0, Mana: 0, Damage: 0, DamageType: ComboDamageType.Physical,
        RatioAD: 0, RatioBonusAD: 0, RatioAP: 0, CastTime: 0, Delay: 0, TravelTime: 0);

    /// <summary>No enemy on board → FallbackDefender (0 armor / 0 MR): every hit lands unmitigated,
    /// so the combo total is the raw sum of each hit's computed value.</summary>
    private static GameSnapshot Snapshot(string activeChampion, ActivePlayerStats stats, int level)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerSummonerName = "Me",
            Level = level,
            PlayerCount = 1,
            Stats = stats,
        };
        snap.Players[0].SummonerName = "Me";
        snap.Players[0].ChampionName = activeChampion;
        snap.Players[0].Team = "ORDER";
        snap.Players[0].Level = level;
        return snap;
    }

    /// <summary>Builds and fires a combo of the given nodes (slot "AA" → auto-attack node, any other
    /// slot → skill node), returning the published ComboResult.</summary>
    private ComboResult RunCombo(string championId, string[] slots, GameSnapshot snap)
    {
        using var config = new ConfigManager(_configPath);
        var engine = new ComboEngine(new DamageEngine(), new RuneEngine());
        var editor = new ComboEditor(engine, config);

        var draft = editor.CreateCombo(championId, "c");
        foreach (var s in slots)
            editor.AddNode(draft.Id, s == "AA" ? AaNode() : SkillNode(s));
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

    // ── Locke: the exact reported scenario ────────────────────────────────────────────

    [Fact]
    public void Locke_SingleAutoAttack_IsAdPlusPassiveOnHitOnly_NotAbilityNail()
    {
        // Reproduces the user's report: Locke AD 73 / AP 18 / Lv 7, a combo of ONE plain auto.
        // Correct total = AD + P "Silver Stake" on-hit (~92). The bug added Q "Soul Nail" (NailDamage,
        // an empowered attack that only exists AFTER a Q cast) to give ~243.
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        const int level = 7;
        var stats = new ActivePlayerStats { AttackDamage = 73, AbilityPower = 18, AbilityQ = 5 };

        // The always-on passive (P) and the empowered ability on-hit (Q nail), resolved live from the
        // same BIN the combo uses — the P must be counted, the nail must NOT (no Q was cast).
        double passiveOnHit = SkillDamage.ComputeCalcDamage(locke, "P", "MinOnHitDamage", stats, level)!.Value;
        double nail = SkillDamage.ComputeCalcDamage(locke, "Q", "NailDamage", stats, level)!.Value;
        Assert.True(nail > 1, "the Q nail must be a real, large hit so its wrongful inclusion is detectable");

        var snap = Snapshot("Locke", stats, level);
        var result = RunCombo("Locke", new[] { "AA" }, snap);

        // AA (physical AD, unmitigated) + P on-hit only. NOT the nail (that would be the 243 bug).
        Assert.Equal(73.0 + passiveOnHit, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count); // AA hit + P on-hit; no third (nail) hit
    }

    [Fact]
    public void Locke_NailEmpowersOnlyTheAutoThatFollowsQ_OrderSensitive()
    {
        // The nail must attach to an auto that FOLLOWS Q, and not to one that precedes it. Both combos
        // carry the identical Q (its direct MissileDamage cancels) and the identical single auto + P
        // on-hit, so their difference is EXACTLY one nail — proving both the "applies after Q" and the
        // "does not apply before Q" halves of the fix at once.
        var locke = LoadChampionFromBin("Locke");
        ChampionRepository.Initialize(new[] { locke });

        const int level = 7;
        var stats = new ActivePlayerStats { AttackDamage = 73, AbilityPower = 18, AbilityQ = 5 };
        double nail = SkillDamage.ComputeCalcDamage(locke, "Q", "NailDamage", stats, level)!.Value;

        var afterQ = RunCombo("Locke", new[] { "Q", "AA" }, Snapshot("Locke", stats, level));
        var beforeQ = RunCombo("Locke", new[] { "AA", "Q" }, Snapshot("Locke", stats, level));

        Assert.Equal(nail, afterQ.TotalDamage - beforeQ.TotalDamage, precision: 2);
        // The trailing auto in [Q, AA] adds one more hit (the nail) than the leading auto in [AA, Q].
        Assert.Equal(beforeQ.NodeBreakdown.Count + 1, afterQ.NodeBreakdown.Count);
    }

    // ── Viktor: a second ability-slot on-hit champion (Q "Discharge"), and no P on-hit ──

    [Fact]
    public void Viktor_SingleAutoAttack_IsPlainAdOnly_NotQDischarge()
    {
        // Viktor has no damaging passive, so a plain auto with no ability cast is pure AD. The bug
        // added Q "Discharge" (AttackTotalDMG, the empowered next-attack) to every auto.
        var viktor = LoadChampionFromBin("Viktor");
        ChampionRepository.Initialize(new[] { viktor });

        const int level = 6;
        var stats = new ActivePlayerStats { AttackDamage = 60, AbilityPower = 80, AbilityQ = 5 };
        double discharge = SkillDamage.ComputeCalcDamage(viktor, "Q", "AttackTotalDMG", stats, level)!.Value;
        Assert.True(discharge > 1, "the Q discharge must be a real hit so its wrongful inclusion is detectable");

        var result = RunCombo("Viktor", new[] { "AA" }, Snapshot("Viktor", stats, level));

        Assert.Equal(60.0, result.TotalDamage, precision: 2); // pure AD, no discharge
        Assert.Single(result.NodeBreakdown);                  // the AA hit only
    }

    [Fact]
    public void Viktor_DischargeEmpowersOnlyTheAutoThatFollowsQ_OrderSensitive()
    {
        var viktor = LoadChampionFromBin("Viktor");
        ChampionRepository.Initialize(new[] { viktor });

        const int level = 6;
        var stats = new ActivePlayerStats { AttackDamage = 60, AbilityPower = 80, AbilityQ = 5 };
        double discharge = SkillDamage.ComputeCalcDamage(viktor, "Q", "AttackTotalDMG", stats, level)!.Value;

        var afterQ = RunCombo("Viktor", new[] { "Q", "AA" }, Snapshot("Viktor", stats, level));
        var beforeQ = RunCombo("Viktor", new[] { "AA", "Q" }, Snapshot("Viktor", stats, level));

        Assert.Equal(discharge, afterQ.TotalDamage - beforeQ.TotalDamage, precision: 2);
        Assert.Equal(beforeQ.NodeBreakdown.Count + 1, afterQ.NodeBreakdown.Count);
    }

    // ── The flip side: a TRUE always-on ability-slot passive still fires on a bare auto ──

    [Fact]
    public void Varus_BlightPassive_IsAlwaysOn_AppliesToBareAuto_WithoutCastingW()
    {
        // Varus W "Blighted Quiver" applies Blight on EVERY basic attack — a passive on-hit that lives
        // under the W slot in the BIN but is not an empowered-after-cast effect (the curation flags it
        // alwaysOn, "same pattern as Warwick P"). The §10 cast-gate must NOT suppress it on a plain
        // auto: this pins the always-on exemption so the fix doesn't silently under-count Varus.
        var varus = LoadChampionFromBin("Varus");
        ChampionRepository.Initialize(new[] { varus });

        const int level = 6;
        var stats = new ActivePlayerStats { AttackDamage = 60, AbilityPower = 100, AbilityW = 5 };
        double blight = SkillDamage.ComputeCalcDamage(varus, "W", "OnHitDamage", stats, level)!.Value;
        Assert.True(blight > 1, "Blight on-hit must resolve to a real value from the W slot's BIN calc");

        var result = RunCombo("Varus", new[] { "AA" }, Snapshot("Varus", stats, level));

        // AA (physical AD) + Blight on-hit (magic) — the ability-slot passive fires with no W cast.
        Assert.Equal(60.0 + blight, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count);
    }

    // ── §11.A: broaden the always-on / empowered coverage across more champions ─────

    [Fact]
    public void Kennen_ElectricalSurgePassive_IsAlwaysOn_AppliesToBareAuto()
    {
        // Kennen W's passive half (TotalDamagePassive) is an always-on on-hit (curated alwaysOn,
        // "same pattern as Warwick P"); it must fire on a plain auto with no W cast. Kennen has no
        // damaging passive slot, so a bare auto is AD + this one on-hit.
        var kennen = LoadChampionFromBin("Kennen");
        ChampionRepository.Initialize(new[] { kennen });

        const int level = 9;
        var stats = new ActivePlayerStats { AttackDamage = 70, AbilityPower = 120, AbilityW = 5 };
        double passive = SkillDamage.ComputeCalcDamage(kennen, "W", "TotalDamagePassive", stats, level)!.Value;
        Assert.True(passive > 1, "Kennen W passive on-hit must resolve to a real value");

        var result = RunCombo("Kennen", new[] { "AA" }, Snapshot("Kennen", stats, level));

        Assert.Equal(70.0 + passive, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count);
    }

    [Fact]
    public void Blitzcrank_StaticFieldPassive_IsAlwaysOn_AppliesToBareAuto()
    {
        // Blitz R's on-hit "Static Field" passive fires whenever R is off cooldown — the DEFAULT
        // R-available state, not only after an R cast. So it is always-on for a plain auto. (§11.A
        // corrected this from §10's cautious cast-gating, which under-counted the common bare-auto case.)
        var blitz = LoadChampionFromBin("Blitzcrank");
        ChampionRepository.Initialize(new[] { blitz });

        const int level = 11;
        var stats = new ActivePlayerStats { AttackDamage = 75, AbilityPower = 60, AbilityR = 1 };
        double passive = SkillDamage.ComputeCalcDamage(blitz, "R", "PassiveDamage", stats, level)!.Value;
        Assert.True(passive > 1, "Blitz R passive on-hit must resolve to a real value");

        var result = RunCombo("Blitzcrank", new[] { "AA" }, Snapshot("Blitzcrank", stats, level));

        Assert.Equal(75.0 + passive, result.TotalDamage, precision: 2);
        Assert.Equal(2, result.NodeBreakdown.Count);
    }

    [Fact]
    public void Rengar_SavageryEmpowersOnlyTheAutoThatFollowsQ_OrderSensitive()
    {
        // A third empowered champion (beyond Locke/Viktor): Rengar Q "Savagery" empowers only the
        // next auto. Its whole payload IS that empowered attack (Q has no direct hit), and any Q-node
        // fallback damage is identical in both orderings, so [Q,AA]-[AA,Q] is exactly one QBonusDamage.
        var rengar = LoadChampionFromBin("Rengar");
        ChampionRepository.Initialize(new[] { rengar });

        const int level = 6;
        var stats = new ActivePlayerStats { AttackDamage = 80, AbilityPower = 0, AbilityQ = 5 };
        double qBonus = SkillDamage.ComputeCalcDamage(rengar, "Q", "QBonusDamage", stats, level)!.Value;
        Assert.True(qBonus > 1, "Rengar Q empowered hit must resolve to a real value");

        var afterQ = RunCombo("Rengar", new[] { "Q", "AA" }, Snapshot("Rengar", stats, level));
        var beforeQ = RunCombo("Rengar", new[] { "AA", "Q" }, Snapshot("Rengar", stats, level));
        Assert.Equal(qBonus, afterQ.TotalDamage - beforeQ.TotalDamage, precision: 2);

        // A bare auto with no Q is pure AD — the empowerment does not leak onto it.
        var bare = RunCombo("Rengar", new[] { "AA" }, Snapshot("Rengar", stats, level));
        Assert.Equal(80.0, bare.TotalDamage, precision: 2);
    }
}
