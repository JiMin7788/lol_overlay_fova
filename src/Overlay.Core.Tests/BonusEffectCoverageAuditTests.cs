using System.Text.Json;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;
using Overlay.Core.Items;

namespace Overlay.Core.Tests;

/// <summary>
/// M23 Phase 2 Step 6 — the completeness guard. Loads the ACTUAL curated corpus
/// (every <c>skill_damage/*.json</c> bonus effect + every <c>item_effects.json</c> proc) plus the
/// two effect-trigger enums, normalizes each through <see cref="BonusEffectNormalizers"/>, and
/// asserts the resulting <c>(EffectTrigger, ConditionType?)</c> is a member of the closed M23
/// archetype registry (docs/modules/M23_EFFECT_CATALOG.md rows A1/A4-manual/A5/A6/A7/A8). Green
/// today; goes red the moment a future patch adds a curated effect whose trigger no normalizer
/// classifies — forcing a catalog entry rather than a silent gap.
///
/// Scope note (honesty): the auto-trigger RUNE engine is classified but stays in Stage B (see
/// <see cref="ComboEngine.AutoTriggeredRuneIds"/>'s doc comment). This audit only verifies those 6
/// ids are backed by real <c>rune_effects.json</c> data. The 8 MANUAL runes are deliberately NOT
/// asserted here: their exact conditions (Cheap Shot VsImpaired, Sudden Impact AfterDash, First
/// Strike FirstHit) are documented OPEN gaps in docs/modules/M23_EFFECT_CATALOG.md — its §1 registry
/// table rows A12-A14 (marked "GAP") and its §5 "Immediate gaps to curate" list — NOT yet on this
/// closed registry, so claiming them "covered" would be false. They enter this audit only after
/// M23 Phase 3 curates those conditions (VsImpaired/AfterDash/FirstHit), at which point the rows
/// move off "GAP" in the catalog and get added to <see cref="Registry"/> here.
/// </summary>
public class BonusEffectCoverageAuditTests
{
    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");
    private static string SkillDir => Path.Combine(DataDir, "skill_damage");

    /// <summary>The CLOSED set of normalized archetypes the Stage-A application pass handles today.
    /// A normalized effect outside this set = an unclassified gap = a failed audit.</summary>
    private static readonly HashSet<(EffectTrigger, ConditionType?)> Registry = new()
    {
        (EffectTrigger.Self, null),                              // A1 (skill self), A4 (item manualActiveBurst)
        (EffectTrigger.OnBasicAttack, null),                    // A5 (skill/item on-hit)
        (EffectTrigger.OnAbilityHit, null),                     // A8 (skill on-ability)
        (EffectTrigger.OnHitEmpowered, ConditionType.OnHitEmpowered), // A7 (item spellblade)
        (EffectTrigger.OnBasicAttack, ConditionType.EveryNth),  // A6 (item stack-then-consume)
    };

    private static (EffectTrigger, ConditionType?) Key(BonusEffect e) => (e.Trigger, e.Condition?.Type);

    [Fact]
    public void EverySkillBonusEffectTrigger_InCuratedCorpus_ClassifiesToARegisteredArchetype()
    {
        Assert.True(Directory.Exists(SkillDir), $"skill_damage dir missing: {SkillDir}");
        var files = Directory.GetFiles(SkillDir, "*.json");
        Assert.NotEmpty(files); // the corpus must actually be deployed, or the audit is vacuous

        int checkedEffects = 0;
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var slotProp in doc.RootElement.EnumerateObject())
            {
                if (slotProp.Value.ValueKind != JsonValueKind.Object) continue; // skip "_note*" strings
                if (!slotProp.Value.TryGetProperty("bonusEffects", out var bonusEffects)) continue;
                foreach (var effectEl in bonusEffects.EnumerateArray())
                {
                    var triggerStr = effectEl.GetProperty("trigger").GetString()!;
                    var trigger = Enum.Parse<BonusTrigger>(triggerStr, ignoreCase: true);
                    var normalized = BonusEffectNormalizers.FromSkillBonusEffect(new SkillBonusEffect { Trigger = trigger });

                    Assert.True(Registry.Contains(Key(normalized)),
                        $"Unclassified skill bonus effect in {Path.GetFileName(file)} slot '{slotProp.Name}': " +
                        $"trigger '{triggerStr}' normalized to {Key(normalized)} which is not in the M23 registry.");
                    checkedEffects++;
                }
            }
        }
        Assert.True(checkedEffects > 0, "audit found no skill bonus effects to check — corpus/parse regression");
    }

    [Fact]
    public void EveryItemProcTrigger_InCuratedCorpus_ClassifiesToARegisteredArchetype()
    {
        var itemFile = Path.Combine(DataDir, "item_effects.json");
        Assert.True(File.Exists(itemFile), $"item_effects.json missing: {itemFile}");

        using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
        Assert.True(doc.RootElement.TryGetProperty("items", out var items), "item_effects.json has no 'items' object");

        int checkedItems = 0;
        foreach (var itemProp in items.EnumerateObject())
        {
            var triggerStr = itemProp.Value.GetProperty("trigger").GetString()!;
            var trigger = Enum.Parse<ItemTrigger>(triggerStr, ignoreCase: true);
            int stacksRequired = itemProp.Value.TryGetProperty("stacksRequired", out var sr) ? sr.GetInt32() : 0;
            var effect = new ItemEffect(itemProp.Name, trigger, HitDamageType.Magic, new SkillData(), "x", StacksRequired: stacksRequired);
            var normalized = BonusEffectNormalizers.FromItemEffect(effect);

            Assert.True(Registry.Contains(Key(normalized)),
                $"Unclassified item proc {itemProp.Name}: trigger '{triggerStr}' normalized to {Key(normalized)} " +
                "which is not in the M23 registry.");
            checkedItems++;
        }
        Assert.True(checkedItems > 0, "audit found no item procs to check — corpus/parse regression");
    }

    [Fact]
    public void EveryDefinedTriggerEnumValue_NormalizesToARegisteredArchetype()
    {
        // Enum-level guard: catches a FUTURE enum value added without a normalizer/registry entry,
        // even before any data file uses it.
        foreach (BonusTrigger t in Enum.GetValues<BonusTrigger>())
        {
            var normalized = BonusEffectNormalizers.FromSkillBonusEffect(new SkillBonusEffect { Trigger = t });
            Assert.True(Registry.Contains(Key(normalized)),
                $"BonusTrigger.{t} normalizes to {Key(normalized)}, not in the M23 registry.");
        }
        foreach (ItemTrigger t in Enum.GetValues<ItemTrigger>())
        {
            var effect = new ItemEffect("x", t, HitDamageType.Magic, new SkillData(), "x", StacksRequired: 2);
            var normalized = BonusEffectNormalizers.FromItemEffect(effect);
            Assert.True(Registry.Contains(Key(normalized)),
                $"ItemTrigger.{t} normalizes to {Key(normalized)}, not in the M23 registry.");
        }
    }

    [Fact]
    public void EveryAutoTriggerRuneId_IsBackedByRuneEffectsData()
    {
        // Stage-B classification <-> data consistency: each auto-trigger rune the engine classifies
        // (Source=Rune, see ComboEngine.AutoTriggeredRuneIds's doc comment) must correspond to a real
        // rune_effects.json entry. Manual runes (A12-A14 gaps) are intentionally NOT checked — see class doc.
        var runeFile = Path.Combine(DataDir, "rune_effects.json");
        Assert.True(File.Exists(runeFile), $"rune_effects.json missing: {runeFile}");

        using var doc = JsonDocument.Parse(File.ReadAllText(runeFile));
        Assert.True(doc.RootElement.TryGetProperty("runes", out var runes), "rune_effects.json has no 'runes' object");

        foreach (var id in ComboEngine.AutoTriggeredRuneIds)
        {
            Assert.True(runes.TryGetProperty(id.ToString(), out _),
                $"Auto-trigger rune id {id} is classified in ComboEngine.AutoTriggeredRuneIds but absent from rune_effects.json.");
        }
    }
}
