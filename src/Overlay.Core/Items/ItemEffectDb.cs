using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.ChampionDb;
using Overlay.Core.Combo;

namespace Overlay.Core.Items;

/// <summary>
/// Loads the bundled, patch-updatable item-proc map (<c>data/item_effects.json</c>, generated
/// by <c>tools/ChampionDataGen -- itemeffects</c> from CommunityDragon's items BIN) into
/// evaluable <see cref="SkillData"/>-shaped structures, mirroring
/// <see cref="Overlay.Core.Combo.SkillDamageDb"/>. Each covered item carries its proc's raw
/// DataValues + one <c>mSpellCalculations</c> GameCalculation, so the number is resolved LIVE
/// against the player's current stats via <see cref="FormulaInterpreter"/> — nothing is
/// pre-computed (Hard Rule: patch-dependent values are always a dynamic lookup).
///
/// Covered items are the ones whose proc calc could be located and evaluated in the BIN
/// (Nashor 3115 / Guinsoo 3124 on-hit; Sheen 3057 / Trinity 3078 / Lich Bane 3100 spellblade),
/// PLUS Wit's End (3091 on-hit), Blade of the Ruined King (3153 on-hit), Titanic Hydra
/// (3748 on-hit) and Kraken Slayer (3095 stack-then-consume), which are hand-authored from live
/// wiki.leagueoflegends.com fetches instead of the BIN (raw.communitydragon.org was unreachable)
/// — see item_effects.json's top-level and per-item "_note" for each item's citation and
/// re-check caveat. Blade of the Ruined King and Titanic Hydra are additionally melee/ranged
/// %-HP items (see <see cref="ItemHpPercentBasis"/>) that bypass FormulaInterpreter entirely,
/// since their proc value is a flat wiki-sourced fraction with no stat scaling; Kraken Slayer
/// is a melee/ranged flat, level-interpolated base number (see <see cref="ItemTrigger.StackThenConsume"/>)
/// gated on the combo's own AA count rather than applied to every AA.
/// PLUS Deathfire Grasp (3128, <see cref="ItemTrigger.ManualActiveBurst"/>), the first item that is
/// NOT applied automatically across the whole build — it only fires on the ONE combo node the user
/// drag-attaches it to (<c>ComboNode.AttachedItemId</c>), consumed by
/// <see cref="Combo.ComboEngine"/>'s <c>ApplyAttachedItemEffects</c>, not
/// <see cref="ComboRunner"/>'s build-list proc pipeline — see that method's doc comment for the
/// instant-burst-plus-damage-amplification-window mechanics.
/// An unknown/uncovered item id simply returns <c>null</c>. Tolerant of a missing/corrupt file
/// (returns empty), so a build with no proc item is a no-op — see <see cref="ComboRunner"/>.
/// </summary>
public static class ItemEffectDb
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Dictionary<string, ItemEffect>? _cache;
    private static readonly object Gate = new();

    /// <summary>Location of the bundled map next to the assembly (same convention as the
    /// skill_damage / ddragon / communitydragon caches — see Overlay.Core.csproj copy rules).</summary>
    private static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "item_effects.json");

    /// <summary>The proc effect for one item id, or <c>null</c> when the item has no covered
    /// proc (unknown id, or a stat-only item). Never throws.</summary>
    public static ItemEffect? Get(string itemId)
        => Load().TryGetValue(itemId, out var e) ? e : null;

    /// <summary>All covered item effects (test/introspection helper).</summary>
    public static IReadOnlyCollection<ItemEffect> All() => Load().Values;

    private static Dictionary<string, ItemEffect> Load()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            var parsed = new Dictionary<string, ItemEffect>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(DefaultPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(DefaultPath));
                    if (doc.RootElement.TryGetProperty("items", out var items)
                        && items.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var item in items.EnumerateObject())
                            if (TryParseEffect(item.Name, item.Value, out var effect))
                                parsed[item.Name] = effect!;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                parsed.Clear(); // treat any load/parse failure as "no item effects"
            }

            _cache = parsed;
            return _cache;
        }
    }

    private static bool TryParseEffect(string itemId, JsonElement obj, out ItemEffect? effect)
    {
        effect = null;
        try
        {
            var trigger = obj.GetProperty("trigger").Deserialize<ItemTrigger>(JsonOptions);
            var type = obj.GetProperty("damageType").Deserialize<HitDamageType>(JsonOptions);
            var name = obj.TryGetProperty("name", out var n) ? (n.GetString() ?? itemId) : itemId;

            // ── stack-then-consume items (Kraken Slayer) ────────────────────────────────────
            // A flat melee/ranged base damage that linearly interpolates level 1→18 (same shape
            // as RuneEffectDb.Evaluate's BaseAtLevel1/BaseAtLevel18 term — see ComboRunner.
            // InterpolateByLevel), triggered every (stacksRequired+1)th AA in the combo's own hit
            // sequence rather than on every AA. No BIN calc: "spell"/"calc" are absent here too.
            if (trigger == ItemTrigger.StackThenConsume)
            {
                if (!obj.TryGetProperty("stacksRequired", out var stacksEl) || stacksEl.ValueKind != JsonValueKind.Number)
                    return false;
                int stacksRequired = stacksEl.GetInt32();

                double? meleeLvl1 = obj.TryGetProperty("meleeDamageAtLevel1", out var ml1) ? ml1.GetDouble() : null;
                double? meleeLvl18 = obj.TryGetProperty("meleeDamageAtLevel18", out var ml18) ? ml18.GetDouble() : null;
                double? rangedLvl1 = obj.TryGetProperty("rangedDamageAtLevel1", out var rl1) ? rl1.GetDouble() : null;
                double? rangedLvl18 = obj.TryGetProperty("rangedDamageAtLevel18", out var rl18) ? rl18.GetDouble() : null;
                if (stacksRequired <= 0 || meleeLvl1 is not > 0 || meleeLvl18 is not > 0
                    || rangedLvl1 is not > 0 || rangedLvl18 is not > 0)
                    return false;

                // Optional: the target's-missing-health scaling term (Kraken Slayer's real
                // "increased by 0%-75% based on target's missing health" clause — see
                // Damage.ExecuteType.BaseWithMissingHpBonus). Absent/0 for any future
                // stack-then-consume item that has no such term.
                double? missingHpBonusScalar = obj.TryGetProperty("missingHpBonusScalar", out var mhb) && mhb.ValueKind == JsonValueKind.Number
                    ? mhb.GetDouble()
                    : null;

                var emptySkill = new SkillData { Key = itemId, Name = name };
                effect = new ItemEffect(itemId, trigger, type, emptySkill, Calc: string.Empty,
                    StacksRequired: stacksRequired,
                    MeleeDamageAtLevel1: meleeLvl1, MeleeDamageAtLevel18: meleeLvl18,
                    RangedDamageAtLevel1: rangedLvl1, RangedDamageAtLevel18: rangedLvl18,
                    MissingHpBonusScalar: missingHpBonusScalar);
                return true;
            }

            // ── manual, node-attached active-burst item (Deathfire Grasp) ──────────────────
            // Not applied automatically across the build like every other trigger above — only
            // consumed when the user drag-attaches this item to a specific combo node
            // (ComboNode.AttachedItemId). Two flat, wiki-sourced fractions (no BIN calc, no
            // AD/AP scaling): targetMaxHpPercent is an instant magic burst on the attached node
            // itself; amplifyDamagePercent/amplifyDurationSeconds describe the target-side
            // "increased damage taken" debuff that follows — see
            // Combo.ComboEngine.ApplyAttachedItemEffects for how the window is applied to the
            // combo's OTHER nodes. amplify fields are optional (a future ManualActiveBurst item
            // with no amplify component would simply omit them).
            if (trigger == ItemTrigger.ManualActiveBurst)
            {
                double? targetMaxHpPercent = obj.TryGetProperty("targetMaxHpPercent", out var tmhp) ? tmhp.GetDouble() : null;
                if (targetMaxHpPercent is not > 0) return false;

                double? amplifyDamagePercent = obj.TryGetProperty("amplifyDamagePercent", out var adp) ? adp.GetDouble() : null;
                double? amplifyDurationSeconds = obj.TryGetProperty("amplifyDurationSeconds", out var ads) ? ads.GetDouble() : null;

                var emptySkill = new SkillData { Key = itemId, Name = name };
                effect = new ItemEffect(itemId, trigger, type, emptySkill, Calc: string.Empty,
                    TargetMaxHpPercent: targetMaxHpPercent,
                    AmplifyDamagePercent: amplifyDamagePercent,
                    AmplifyDurationSeconds: amplifyDurationSeconds);
                return true;
            }

            // ── melee/ranged %-HP items (Blade of the Ruined King, Titanic Hydra) ──────────
            // These procs are a flat fraction of a HP value (target's current HP, or the
            // caster's own max HP) that differs by attack type; they carry no AD/AP/other stat
            // scaling, so there is no BIN GameCalculation to evaluate — "spell"/"calc" are not
            // present for these entries and FormulaInterpreter is bypassed entirely (see
            // ComboRunner.ResolveBuildProcs). This is the same "hand-authored, wiki-sourced,
            // needs manual re-check" convention as Wit's End (3091), just without a fake
            // single-DataValue formula shape since none of FormulaInterpreter's stat scaling
            // applies here.
            if (obj.TryGetProperty("hpPercentBasis", out var basisEl) && basisEl.ValueKind == JsonValueKind.String)
            {
                var basis = basisEl.Deserialize<ItemHpPercentBasis>(JsonOptions);
                double? meleePct = obj.TryGetProperty("meleeHpPercent", out var m) ? m.GetDouble() : null;
                double? rangedPct = obj.TryGetProperty("rangedHpPercent", out var r) ? r.GetDouble() : null;
                if (meleePct is not > 0 || rangedPct is not > 0) return false;

                var emptySkill = new SkillData { Key = itemId, Name = name };
                effect = new ItemEffect(itemId, trigger, type, emptySkill, Calc: string.Empty,
                    HpPercentBasis: basis, MeleeHpPercent: meleePct, RangedHpPercent: rangedPct);
                return true;
            }

            var calc = obj.GetProperty("calc").GetString();
            if (string.IsNullOrEmpty(calc)) return false;

            // The "spell" object carries the raw BIN sub-shape (DataValues + mSpellCalculations);
            // reuse the champion BIN parser so item procs evaluate through the same interpreter.
            var bin = ChampionBinParser.ParseSpell(obj.GetProperty("spell"));
            if (!bin.SpellCalculations.ContainsKey(calc)) return false;

            var skill = new SkillData
            {
                Key = itemId,
                Name = name,
                DataValues = bin.DataValues,
                SpellCalculations = bin.SpellCalculations,
                EffectAmounts = bin.EffectAmounts,
            };
            effect = new ItemEffect(itemId, trigger, type, skill, calc);
            return true;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Test-only reset so the lazy cache doesn't leak between cases.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _cache = null;
    }
}

/// <summary>When an item's proc applies within a combo:
/// <see cref="OnHit"/> = added to every auto-attack (Nashor/Guinsoo);
/// <see cref="Spellblade"/> = a single shared unique proc on an ability→auto-attack transition
/// (Sheen/Trinity/Lich Bane); <see cref="StackThenConsume"/> = a stack builds on every on-hit AA
/// (up to <see cref="ItemEffect.StacksRequired"/>) and is consumed on the AA that reaches the cap,
/// repeating for the rest of the combo (Kraken Slayer's Bring It Down — see
/// ComboRunner.ResolveBuildProcs/BuildStackThenConsumeItemProc). <see cref="ManualActiveBurst"/> =
/// NOT applied automatically anywhere in the build — only fires on the single combo node the user
/// drag-attaches the item to (<c>ComboNode.AttachedItemId</c>), consumed by
/// <see cref="Combo.ComboEngine"/>'s <c>ApplyAttachedItemEffects</c> instead of the build-list
/// pipeline above (Deathfire Grasp's "The Silence" active — see
/// <see cref="ItemEffect.TargetMaxHpPercent"/>/<see cref="ItemEffect.AmplifyDamagePercent"/>).</summary>
public enum ItemTrigger { OnHit, Spellblade, StackThenConsume, ManualActiveBurst }

/// <summary>Which HP value a melee/ranged %-HP item proc (see <see cref="ItemEffect.MeleeHpPercent"/>/
/// <see cref="ItemEffect.RangedHpPercent"/>) is a fraction of. <see cref="None"/> = not a %-HP item
/// (the ordinary BIN-calc path, e.g. Nashor/Sheen). <see cref="TargetCurrent"/> = Blade of the Ruined
/// King's Mist's Edge — a fraction of the TARGET's current HP, re-evaluated live as it drops within the
/// combo sequence (reuses the exact same <see cref="Combo.ComboExecuteType.CurrentHp"/> mechanism
/// skill %HP hits like Vayne W/Brand P/Skarner P already use — see ComboRunner.TryBuildHitShape).
/// <see cref="CasterMax"/> = Titanic Hydra's Cleave — a fraction of the CASTER's own max HP (constant
/// across the combo, so it resolves to a flat number once, like a spellblade proc).</summary>
public enum ItemHpPercentBasis { None, TargetCurrent, CasterMax }

/// <summary>One covered item proc: its trigger, curated single-hit damage <see cref="DamageType"/>
/// (not carried by the BIN calc), and the evaluable <see cref="SkillData"/> + <see cref="Calc"/>
/// name whose number FormulaInterpreter resolves live from the current-patch BIN. For a melee/ranged
/// %-HP item (<see cref="HpPercentBasis"/> != <see cref="ItemHpPercentBasis.None"/>), <see cref="Skill"/>/
/// <see cref="Calc"/> are unused placeholders — the number instead comes from
/// <see cref="MeleeHpPercent"/>/<see cref="RangedHpPercent"/> (wiki-sourced literal fractions, chosen
/// by the ACTIVE player's own champion being melee or ranged — see ComboRunner.IsMelee). For a
/// stack-then-consume item (<see cref="Trigger"/> == <see cref="ItemTrigger.StackThenConsume"/>),
/// <see cref="Skill"/>/<see cref="Calc"/> are likewise unused — the number comes from
/// <see cref="StacksRequired"/> (stack count needed before the next AA consumes them) plus the
/// melee/ranged level-1/level-18 base-damage endpoints, linearly interpolated by the caster's live
/// level (see ComboRunner.BuildStackThenConsumeItemProc). <see cref="MissingHpBonusScalar"/> (when
/// present) is the item's real target's-missing-health scaling term (Kraken Slayer: 0.75, i.e.
/// "up to 75% bonus at 0 target HP") — wired into <see cref="Damage.ExecuteType.BaseWithMissingHpBonus"/>
/// so the base number scales dynamically with the live target's missing-HP fraction, same as any
/// other %HP execute node. For a manual active-burst item (<see cref="Trigger"/> ==
/// <see cref="ItemTrigger.ManualActiveBurst"/>), <see cref="Skill"/>/<see cref="Calc"/> are again
/// unused — <see cref="TargetMaxHpPercent"/> is a flat, wiki-sourced fraction of the target's Max
/// Health (Deathfire Grasp: 0.15) that becomes a new trailing burst node keyed to the ONE combo
/// node the item is attached to (never folded into that node's own Damage — its damage TYPE may
/// differ, see Combo.ComboEngine.ApplyAttachedItemEffects's doc comment for why);
/// <see cref="AmplifyDamagePercent"/>/<see cref="AmplifyDurationSeconds"/> (both optional — null
/// means the item has no amplify component) describe a genuine damage-multiplier WINDOW applied to
/// every OTHER node in the same combo that lands within that many seconds after the attached node
/// — see <see cref="Combo.ComboEngine"/>'s <c>ApplyAttachedItemEffects</c> for the window mechanics.</summary>
public sealed record ItemEffect(
    string ItemId, ItemTrigger Trigger, HitDamageType DamageType, SkillData Skill, string Calc,
    ItemHpPercentBasis HpPercentBasis = ItemHpPercentBasis.None,
    double? MeleeHpPercent = null, double? RangedHpPercent = null,
    int? StacksRequired = null,
    double? MeleeDamageAtLevel1 = null, double? MeleeDamageAtLevel18 = null,
    double? RangedDamageAtLevel1 = null, double? RangedDamageAtLevel18 = null,
    double? MissingHpBonusScalar = null,
    double? TargetMaxHpPercent = null,
    double? AmplifyDamagePercent = null, double? AmplifyDurationSeconds = null);
