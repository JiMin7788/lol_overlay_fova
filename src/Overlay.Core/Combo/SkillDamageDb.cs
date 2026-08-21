using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlay.Core.Combo;

/// <summary>
/// Loads the curated per-skill combo-damage map from
/// <c>data/skill_damage/{champion}.json</c> (one file per cached champion), lazily and
/// once per champion. Each file maps a skill slot ("Q"/"W"/"E"/"R", optionally "P") to a
/// list of <see cref="SkillHit"/>s — each hit carries a CURATED damage type, the name of a
/// BIN <c>mSpellCalculations</c> GameCalculation to evaluate for that hit's single-target
/// raw number (via <see cref="ChampionDb.FormulaInterpreter"/>), and a landed-fully hit
/// <c>count</c>. See docs/reports/agent/combo_damage_model_report.md for the schema and the
/// per-skill authoring rationale.
///
/// Why a curated file rather than pure BIN inference: the BIN exposes the numbers (base +
/// ratios) but not, cleanly, the damage TYPE nor how many times a multi-hit skill lands on
/// one target — both of which the accurate combo model needs. This file is the single place
/// that curated knowledge lives; the numbers themselves still come live from the BIN so they
/// track the current patch (Hard Rule: patch-dependent values are always a dynamic lookup).
///
/// Tolerant of a missing/corrupt file: an uncurated champion returns <c>null</c>, and the
/// caller falls back to the legacy single-hit heuristic so the HUD still shows something.
/// </summary>
public static class SkillDamageDb
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Champion id (case-insensitive) -> (slot -> curated slot), or null cached for "no such file".
    private static readonly Dictionary<string, Dictionary<string, CuratedSlot>?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>Directory holding the curated files, resolved next to the assembly the same
    /// way the BIN/DDragon caches are (see Overlay.Core.csproj copy rules).</summary>
    private static string DefaultDir => Path.Combine(AppContext.BaseDirectory, "data", "skill_damage");

    /// <summary>Returns the curated hits for one skill slot of <paramref name="championId"/>,
    /// or <c>null</c> when the champion has no curated file or the file has no entry for that
    /// slot (e.g. a no-damage utility skill). Never throws — a corrupt file is treated as
    /// "not curated".</summary>
    public static SkillHit[]? GetHits(string championId, string slot)
    {
        var champion = Load(championId);
        if (champion is null) return null;
        return champion.TryGetValue(slot, out var s) && s.Hits.Length > 0 ? s.Hits : null;
    }

    /// <summary>True when the curated file has an ENTRY for this slot at all — even a
    /// bonus-effects-only entry with zero direct hits (Ivern W Brushmaker, Hwei W). Distinguishes
    /// "curation says this cast deals NO direct damage" (authoritative zero — the golden-#16 Ivern
    /// phantom-W fix: the BIN heuristic must NOT fall back onto such a slot, because the shell may
    /// carry the RIDER's calc as a plausible-looking candidate) from "slot simply not curated"
    /// (heuristic fallback allowed, pre-existing semantics).</summary>
    public static bool IsSlotCurated(string championId, string slot)
    {
        var champion = Load(championId);
        return champion is not null && champion.ContainsKey(slot);
    }

    /// <summary>(M22) All curated slot keys for <paramref name="championId"/> — the canonical
    /// P/Q/W/E/R plus any EXTRA multi-form slots (e.g. Jayce "QCannon"/"WCannon"). Used by the
    /// combo-editor palette to offer form/weapon/sub-spell skills as selectable nodes. Empty for an
    /// uncurated champion. Never throws.</summary>
    public static IReadOnlyCollection<string> GetCuratedSlotKeys(string championId)
    {
        var champion = Load(championId);
        return champion is null ? Array.Empty<string>() : champion.Keys.ToArray();
    }

    /// <summary>Returns the curated slot-level tags of one skill slot ("P"/"Q"/
    /// "W"/"E"/"R") of <paramref name="championId"/> — taxonomy classification (e.g. "BurstCeiling",
    /// "Zone") describing the SKILL AS A WHOLE, distinct from a per-hit tag on <see cref="SkillHit.Tags"/>.
    /// See the M21 doc comment near <see cref="SkillHit"/> for the full taxonomy. Returns an empty array
    /// (never null) for an uncurated champion/slot or a slot with no curated tags. Never throws.</summary>
    public static string[] GetSlotTags(string championId, string slot)
    {
        var champion = Load(championId);
        if (champion is null) return Array.Empty<string>();
        return champion.TryGetValue(slot, out var s) ? s.Tags : Array.Empty<string>();
    }

    /// <summary>(loop 175) The CommunityDragon game-asset icon path curated for one slot (relative to
    /// <c>raw.communitydragon.org/latest/game/</c>), or null. Only EXTRA transform-form slots
    /// (Jayce "QCannon", Gnar "QMega", …) carry one — canonical P/Q/W/E/R use the Data Dragon spell
    /// icon and return null here. Used by the combo palette to show a real ability icon instead of a
    /// letter badge for the alternate form. Never throws.</summary>
    public static string? GetSlotIcon(string championId, string slot)
    {
        var champion = Load(championId);
        return champion is not null && champion.TryGetValue(slot, out var s) ? s.Icon : null;
    }

    /// <summary>Number of independent, separately-mitigated casts a curated slot's <see cref="Hits"/>
    /// represent when the skill appears ONCE in a combo (e.g. Ahri R "Spirit Rush" — a single R node
    /// in the combo editor stands for up to 3 real casts). Defaults to 1 (the pre-existing behavior:
    /// one cast) for every slot that doesn't curate <c>"castCount"</c> — never touches an uncurated
    /// or single-cast skill's expansion. Distinct from <see cref="SkillHit.Count"/>, which repeats
    /// ONE hit WITHIN a single cast (e.g. Miss Fortune R's bullets); castCount repeats the whole
    /// per-cast hit set.</summary>
    public static int GetCastCount(string championId, string slot)
    {
        var champion = Load(championId);
        if (champion is not null && champion.TryGetValue(slot, out var s) && s.CastCount > 1)
            return s.CastCount;
        return 1;
    }

    /// <summary>Returns the champion-intrinsic <see cref="SkillBonusEffect"/>s curated on one
    /// slot ("P"/"Q"/"W"/"E"/"R") — extra damage that slot's skill/passive contributes to other
    /// events (an empowered auto-attack on-hit, an on-ability proc, or the skill's own follow-up).
    /// The bonus hits' <see cref="SkillHit.Calc"/>s are BIN calcs of THIS slot's spell (so an
    /// on-hit passive lives under "P"). Returns <c>null</c> when the slot has no bonus effects.
    /// Never throws.</summary>
    public static SkillBonusEffect[]? GetBonusEffects(string championId, string slot)
    {
        var champion = Load(championId);
        if (champion is null) return null;
        return champion.TryGetValue(slot, out var s) && s.BonusEffects.Length > 0 ? s.BonusEffects : null;
    }

    /// <summary>Returns the FIRST duration-scaled hit (<see cref="SkillHit.IsDurationScaled"/>)
    /// curated on one skill slot of <paramref name="championId"/>, or null when the champion/slot
    /// has no such hit. Used by the combo editor's "적중시간" (hit duration) control to decide
    /// whether to offer it on a node and to read <see cref="SkillHit.MaxDurationSeconds"/> for the
    /// slider's upper bound. A slot with more than one duration-scaled hit (none currently curated)
    /// returns only the first — a documented simplification, not a limitation any curated skill
    /// hits today.</summary>
    public static SkillHit? GetDurationScaledHit(string championId, string slot)
        => GetHits(championId, slot)?.FirstOrDefault(h => h.IsDurationScaled);

    /// <summary>(M22 Phase 3) The first per-attack SUMMON hit (<see cref="SkillHit.PerAttackCalc"/>)
    /// curated on a slot, or null when none. Used by the combo editor's "몇 대" (attack-count) control
    /// to decide whether to offer it on a node (parallel to <see cref="GetDurationScaledHit"/>).</summary>
    public static SkillHit? GetPerAttackHit(string championId, string slot)
        => GetHits(championId, slot)?.FirstOrDefault(h => !string.IsNullOrEmpty(h.PerAttackCalc));

    /// <summary>(M24 P3/P7) The first distance/charge-scaled hit (<see cref="SkillHit.IsDistanceScaled"/>,
    /// e.g. Hecarim E, Fizz R, Nidalee Q) curated on a slot, or null when none. Used by the combo
    /// editor's "거리" (distance) knob to decide whether to offer it on a node (parallel to
    /// <see cref="GetDurationScaledHit"/>/<see cref="GetPerAttackHit"/>).</summary>
    public static SkillHit? GetDistanceScaledHit(string championId, string slot)
        => GetHits(championId, slot)?.FirstOrDefault(h => h.IsDistanceScaled);

    /// <summary>(§19, M28 §1 charge-slider class) The first charge-scaled hit
    /// (<see cref="SkillHit.IsChargeScaled"/>, e.g. K'Sante All-Out W's TRUE rider) curated on a slot,
    /// or null when none. Shares the same node knob (<see cref="ComboNode.UserDistanceFraction"/>) as
    /// a distance-scaled hit — M28 §1 groups charge and distance under one 0-1 slider — so the combo
    /// editor / range-exposure treat the two together (see <see cref="GetDistanceScaledHit"/>).</summary>
    public static SkillHit? GetChargeScaledHit(string championId, string slot)
        => GetHits(championId, slot)?.FirstOrDefault(h => h.IsChargeScaled);

    /// <summary>(M25 §11.G) The first stack-scaled hit (<see cref="SkillHit.StackScaled"/>, e.g. Nasus Q
    /// Siphoning stacks) curated on a slot, or null when none. Used by the combo editor's "몇 스택"
    /// (stack-count) knob to decide whether to offer it on a node (parallel to
    /// <see cref="GetPerAttackHit"/>/<see cref="GetDistanceScaledHit"/>).</summary>
    public static SkillHit? GetStackScaledHit(string championId, string slot)
        => GetHits(championId, slot)?.FirstOrDefault(StackKnobbed)
           // (loop 484) …and the riders that attach here, the same fallback GetConditionalHit uses.
           // Locke's nail consumption is an on-hit rider with no hit of its own on any slot, so the
           // knob has to be offered on the attack that consumes the nails.
           ?? BonusHit(championId, slot, StackKnobbed);

    private static bool StackKnobbed(SkillHit h) => h.StackScaled || h.IsStackTiered;

    /// <summary>(M28 §1 "binary conditional hit" — docs/modules/M28_NODE_OPTION_UX.md) The first
    /// conditional-bonus hit (<see cref="SkillHit.IsConditional"/>) curated on a slot, or null when
    /// none. Used by the combo editor's checkbox to decide whether to offer it on a node (parallel to
    /// <see cref="GetPerAttackHit"/>/<see cref="GetDistanceScaledHit"/>/
    /// <see cref="GetStackScaledHit"/>).
    ///
    /// <para>(loop 472) AutoResolvable conditions — the caster's own resource, e.g. Renekton's fury
    /// or Rengar's ferocity — used to be excluded here, on the reasoning that they resolve live and
    /// so never need asking about. That is true IN GAME and false in the editor, which is where
    /// combos are actually built: with no game running, or with the bar empty, the empowered variant
    /// could not be previewed at all. The knob is now offered for them too and OVERRIDES the live
    /// reading when the user sets it; left unset it still resolves from live state exactly as before
    /// (see ComboRunner's conditional branch), so nothing changes for anyone who does not touch
    /// it.</para></summary>
    public static SkillHit? GetConditionalHit(string championId, string slot)
        => GetHits(championId, slot)?.FirstOrDefault(UsableConditional)
           ?? ConditionalBonusHit(championId, slot);

    private static bool UsableConditional(SkillHit h)
        => h.IsConditional && Enum.TryParse<ConditionType>(h.ConditionType, out _);

    /// <summary>(loop 479) The same search over the RIDERS that attach to this slot rather than its own
    /// hits — a conditional bonus effect, e.g. Jhin's fourth shot, which is a %missing-health rider on
    /// every auto-attack and so has no hit of its own on any Q/W/E/R slot to carry the checkbox.
    ///
    /// <para>Filtered by <see cref="SkillBonusEffect.AppliesToSlot"/>, so an on-hit rider offers its
    /// checkbox on AA and an on-ability one on the abilities — the same membership rule that decides
    /// where the rider's damage lands, so the toggle can never appear somewhere the hit cannot.</para>
    /// </summary>
    private static SkillHit? ConditionalBonusHit(string championId, string slot)
        => BonusHit(championId, slot, UsableConditional);

    /// <summary>(loop 484) The first hit matching <paramref name="match"/> among the RIDERS that
    /// attach to <paramref name="slot"/> — generalised from the conditional-only search so the stack
    /// knob can use it too.</summary>
    private static SkillHit? BonusHit(string championId, string slot, Func<SkillHit, bool> match)
    {
        var champion = Load(championId);
        if (champion is null) return null;
        foreach (var source in BonusSourceSlots(championId))
        {
            if (!champion.TryGetValue(source, out var s)) continue;
            foreach (var effect in s.BonusEffects)
            {
                if (!effect.AppliesToSlot(slot)) continue;
                foreach (var hit in effect.Hits)
                    if (match(hit)) return hit;
            }
        }
        return null;
    }

    /// <summary>Slots inspected for attachable bonus effects, in display order.</summary>
    private static readonly string[] AttachableSlots = { "P", "Q", "W", "E", "R" };

    /// <summary>(loop 480) Every slot of <paramref name="championId"/> that may carry bonus effects:
    /// the canonical five first (so display order is unchanged), then any EXTRA curated slot.
    ///
    /// <para>Hwei is why. His W is a muse selector that deals nothing; the on-hit empowerment belongs
    /// to the WE sub-cast, which is its own slot. Scanning only the canonical five would have read
    /// that rider as never existing — or, if left on W, fired it after any W, including the two that
    /// are pure utility.</para></summary>
    internal static IEnumerable<string> BonusSourceSlots(string championId)
    {
        foreach (var slot in AttachableSlots) yield return slot;
        foreach (var slot in GetCuratedSlotKeys(championId))
            if (!AttachableSlots.Contains(slot, StringComparer.Ordinal)) yield return slot;
    }

    /// <summary>Enumerates the "bonus-granting" effects of <paramref name="championId"/> that the
    /// combo editor's sub-icon picker can attach under a skill node (T3.3/T8). This is the champion's
    /// own curated <c>bonusEffects</c> across every slot (an on-hit/on-ability passive, e.g. Warwick's
    /// or Mordekaiser's) PLUS its passive "P" direct damage (e.g. Sylas's Petricite Burst) wrapped as a
    /// <see cref="BonusTrigger.Self"/> effect, so a passive that adds damage but was NOT auto-applied is
    /// still attachable manually. Each descriptor keeps the SLOT whose BIN spell holds the hit's calc,
    /// so the number still resolves LIVE from the BIN when applied (never a typed-in constant). Returns
    /// an empty list for an uncurated champion. Never throws.</summary>
    public static IReadOnlyList<AttachableBonusEffect> GetAttachableBonusEffects(string championId)
    {
        var result = new List<AttachableBonusEffect>();
        var champion = Load(championId);
        if (champion is null) return result;

        foreach (var slot in BonusSourceSlots(championId))
        {
            if (!champion.TryGetValue(slot, out var s)) continue;

            // Curated bonus effects on this slot (on-hit / on-ability / self passives).
            foreach (var effect in s.BonusEffects)
                result.Add(new AttachableBonusEffect(slot, effect));

            // The passive's own direct damage (Sylas/Darius): a bonus a user can attach under a node.
            // Unless it is not a rider at all — Kog'Maw's Icathian Surprise is an ON-DEATH explosion,
            // so offering it as something to hang under a skill was never meaningful. Such a passive
            // is still addable as its own P node, which is the honest model for it.
            if (slot.Equals("P", StringComparison.OrdinalIgnoreCase) && s.Hits.Length > 0
                && s.SelfAttachable)
                result.Add(new AttachableBonusEffect(
                    slot, new SkillBonusEffect { Trigger = BonusTrigger.Self, Hits = s.Hits }));
        }
        return result;
    }

    /// <summary>(loop 475) The subset of <see cref="GetAttachableBonusEffects(string)"/> that can
    /// actually ride a node in <paramref name="targetSlot"/> ("AA" for a basic attack) — see
    /// <see cref="SkillBonusEffect.AppliesToSlot"/>. Both the manual "+fx" picker and the automatic
    /// on-ability attach go through this, so an effect cannot be added to a node it does not apply
    /// to by either route.</summary>
    public static IReadOnlyList<AttachableBonusEffect> GetAttachableBonusEffects(
        string championId, string targetSlot)
    {
        var all = GetAttachableBonusEffects(championId);
        var result = new List<AttachableBonusEffect>(all.Count);
        foreach (var eff in all)
            if (eff.Effect.AppliesToSlot(targetSlot)) result.Add(eff);
        return result;
    }

    private static Dictionary<string, CuratedSlot>? Load(string championId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(championId, out var cached)) return cached;

            Dictionary<string, CuratedSlot>? parsed = null;
            try
            {
                var path = Path.Combine(DefaultDir, championId + ".json");
                if (File.Exists(path))
                {
                    // Parse values as raw JsonElement so metadata keys like "_note" (a string,
                    // not a slot object) don't break deserialization; only object-valued,
                    // non-underscore keys are treated as skill slots.
                    var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path), JsonOptions);
                    if (raw is not null)
                    {
                        parsed = new Dictionary<string, CuratedSlot>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (slot, value) in raw)
                        {
                            if (slot.StartsWith('_') || value.ValueKind != JsonValueKind.Object) continue;
                            var slotJson = value.Deserialize<SkillSlotJson>(JsonOptions);
                            if (slotJson is null) continue;
                            var hits = slotJson.Hits ?? Array.Empty<SkillHit>();
                            var bonus = slotJson.BonusEffects ?? Array.Empty<SkillBonusEffect>();
                            var tags = slotJson.Tags ?? Array.Empty<string>();
                            int castCount = slotJson.CastCount is > 1 ? slotJson.CastCount.Value : 1;
                            // Keep the slot when it carries direct hits, bonus effects, tags, and/or
                            // an icon (a slot object with none of the four is metadata-only and
                            // ignored). Tags alone (e.g. a pure-utility shred-only skill with no
                            // direct damage hit) must still be kept — see SkillDamageDb.GetSlotTags.
                            //
                            // (loop 480) An ICON alone is a deliberate "show this cast" — Hwei's WQ
                            // and WW are a movement boon and a shield, real casts with real
                            // cooldowns and no damage at all. Dropping them for having no number
                            // would be the same erasure the muse split exists to undo.
                            if (hits.Length > 0 || bonus.Length > 0 || tags.Length > 0
                                || !string.IsNullOrEmpty(slotJson.Icon))
                                parsed[slot] = new CuratedSlot(hits, bonus, castCount, tags, slotJson.Icon,
                                    slotJson.SelfAttachable ?? true);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                parsed = null; // treat any load/parse failure as "not curated"
            }

            Cache[championId] = parsed;
            return parsed;
        }
    }

    /// <summary>Test-only reset so unit tests don't leak the lazy cache between cases.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) Cache.Clear();
    }

    private sealed class SkillSlotJson
    {
        [JsonPropertyName("hits")]
        public SkillHit[]? Hits { get; init; }

        [JsonPropertyName("bonusEffects")]
        public SkillBonusEffect[]? BonusEffects { get; init; }

        /// <summary>Optional: how many independent real casts this slot's <see cref="Hits"/>
        /// represent for a recastable skill (e.g. Ahri R "Spirit Rush" = 3). Null/1/omitted =
        /// the pre-existing single-cast behavior. See <see cref="SkillDamageDb.GetCastCount"/>.</summary>
        [JsonPropertyName("castCount")]
        public int? CastCount { get; init; }

        /// <summary>Optional slot-level taxonomy tags (e.g. <c>["BurstCeiling"]</c>) describing the
        /// skill as a whole rather than one hit. See the M21 taxonomy doc comment near
        /// <see cref="SkillHit"/> and <see cref="SkillDamageDb.GetSlotTags"/>. Null/omitted (the
        /// pre-existing shape) = no slot-level tags.</summary>
        [JsonPropertyName("tags")]
        public string[]? Tags { get; init; }

        /// <summary>(loop 476) False for a passive whose damage is NOT a rider on anything the user
        /// can attach it to — Kog'Maw's Icathian Surprise is an ON-DEATH explosion, so offering it as
        /// something to hang under a skill node was never meaningful. Such a passive stays addable as
        /// its own P node, which is the honest model. Defaults true (every other P direct-damage
        /// passive: Sylas, Darius, Briar).</summary>
        [JsonPropertyName("selfAttachable")]
        public bool? SelfAttachable { get; init; }

        /// <summary>(loop 175) Optional CommunityDragon game-asset icon path for this slot, used by the
        /// combo editor's palette to show a real ability icon for EXTRA (transform-form) slots whose
        /// icon isn't in Data Dragon's base P/Q/W/E/R set — e.g. Jayce "QCannon" -&gt;
        /// "assets/characters/jayce/hud/icons2d/jayceq_ranged.png". Relative to
        /// <c>raw.communitydragon.org/latest/game/</c>. Null/omitted for canonical slots (they use the
        /// Data Dragon spell icon). See <see cref="SkillDamageDb.GetSlotIcon"/>.</summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; init; }
    }

    /// <summary>One parsed skill-slot entry: its direct per-hit damage, any champion-intrinsic
    /// bonus effects, how many independent casts the hits represent (<see cref="CastCount"/>,
    /// default 1), and any slot-level taxonomy <see cref="Tags"/> (always non-null, empty when
    /// uncurated — the caller, <see cref="SkillDamageDb.Load"/>, normalizes a missing/null JSON
    /// "tags" key to <c>Array.Empty&lt;string&gt;()</c> before constructing this). Hits/BonusEffects/
    /// Tags may all be empty (a slot with only bonus effects, or only tags and no direct hits, is
    /// still kept — see <see cref="SkillDamageDb.Load"/>).</summary>
    private readonly record struct CuratedSlot(SkillHit[] Hits, SkillBonusEffect[] BonusEffects, int CastCount, string[] Tags, string? Icon, bool SelfAttachable);
}

/// <summary>A champion-intrinsic bonus damage source curated on a skill slot: extra hits that
/// this slot's skill/passive contributes when its <see cref="Trigger"/> fires. Numbers still
/// resolve live from the BIN (each <see cref="SkillHit.Calc"/> is a calc of the containing
/// slot's spell) — this only stores STRUCTURE (which calc, type, count, when it applies).</summary>
public sealed class SkillBonusEffect
{
    [JsonPropertyName("trigger")]
    public BonusTrigger Trigger { get; init; }

    [JsonPropertyName("hits")]
    public SkillHit[] Hits { get; init; } = Array.Empty<SkillHit>();

    /// <summary>(loop 475) Node slots this effect can actually ride, e.g. <c>["AA", "R"]</c> for Lux's
    /// Illumination, which her next basic attack or her ultimate detonates and nothing else does. The
    /// pseudo-slot <c>"AA"</c> means a basic-attack node.
    ///
    /// <para>Null/omitted falls back to <see cref="AppliesToSlot"/>'s trigger default, which is what
    /// almost every champion wants; curate this only for the real exceptions. Before it existed the
    /// editor offered EVERY effect under EVERY node and auto-attached every on-ability one to all four
    /// abilities, so a passive that detonates on two of them was silently counted on all of them.</para></summary>
    [JsonPropertyName("appliesTo")]
    public string[]? AppliesTo { get; init; }

    /// <summary>(loop 477) How many attacks an EMPOWERED-next-attack rider may ride before it is
    /// spent — 1 for Rengar's Savagery stab, 3 for Evelynn's Hate Spike mark. Zero/omitted means
    /// uncapped, which was the only behaviour before this existed.
    ///
    /// <para>Uncapped is wrong for most of them and was a known over-count: a plain ability-slot
    /// on-hit fires on EVERY auto after that ability was cast, so a Q-A-A-A combo charged Rengar's
    /// single stab three times. Evelynn's curation note had already recorded the same defect against
    /// her 3-proc cap. Ignored for a P-slot or AlwaysOn effect, which genuinely do fire on every
    /// attack.</para></summary>
    [JsonPropertyName("maxProcs")]
    public int MaxProcs { get; init; }

    /// <summary>Whether this effect can ride a node in <paramref name="slot"/> ("AA" for a basic
    /// attack). An explicit <see cref="AppliesTo"/> decides; otherwise the TRIGGER does, which is the
    /// honest default in both directions: an on-hit effect rides attacks, an on-ability effect rides
    /// abilities, and a Self effect (the passive's own damage, attached by hand) rides anything
    /// because the user is asserting when it happened.</summary>
    public bool AppliesToSlot(string slot)
    {
        bool isAttack = slot.Equals("AA", StringComparison.OrdinalIgnoreCase);
        if (AppliesTo is { Length: > 0 })
        {
            foreach (var s in AppliesTo)
                if (s.Equals(slot, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        return Trigger switch
        {
            BonusTrigger.OnHit => isAttack,
            BonusTrigger.OnAbility => !isAttack,
            _ => true,
        };
    }

    /// <summary>(§10) For an <see cref="BonusTrigger.OnHit"/> effect curated under an ABILITY slot
    /// (Q/W/E/R): true = an ALWAYS-ON passive on-hit that fires on every auto-attack regardless of
    /// whether that ability was cast (e.g. Varus W Blight, Kayle E's passive half, Vi W Denting
    /// Blows — a Trait_PassiveSlot); false (default) = an EMPOWERED next-attack that fires only on an
    /// auto FOLLOWING that ability's cast (e.g. Locke Q Soul Nail, Viktor Q Discharge). A P-slot
    /// on-hit is inherently always-on and needs no flag. The calc still resolves against the slot
    /// whose BIN spell defines it — this is a gating hint only, NOT a slot move (moving the calc to P
    /// would orphan it, since ComputeCalcDamage looks it up in that exact slot's spell). Ignored for
    /// non-OnHit triggers.</summary>
    [JsonPropertyName("alwaysOn")]
    public bool AlwaysOn { get; init; }
}

/// <summary>A bonus effect the combo editor can attach under a skill node (T3.3/T8): the champion
/// skill <see cref="Slot"/> whose BIN spell holds the effect's hit calcs, and the reused
/// <see cref="SkillBonusEffect"/> (trigger + hits) itself. Enumerated for the picker by
/// <see cref="SkillDamageDb.GetAttachableBonusEffects"/> and, once the user attaches one, stored on the
/// combo node (<see cref="ComboNode.UserBonusEffects"/>) and merged into damage by <see cref="ComboRunner"/>.
/// Because it carries a calc slot rather than a number, the damage still resolves LIVE from the BIN.</summary>
public sealed record AttachableBonusEffect(
    [property: JsonPropertyName("slot")] string Slot,
    [property: JsonPropertyName("effect")] SkillBonusEffect Effect);

/// <summary>When a <see cref="SkillBonusEffect"/> applies within a combo:
/// <see cref="OnHit"/> = added to every auto-attack (an on-hit passive, e.g. Warwick);
/// <see cref="OnAbility"/> = added to every ability cast (Q/W/E/R);
/// <see cref="Self"/> = added to the node of the same slot the effect is curated on.</summary>
public enum BonusTrigger { Self, OnHit, OnAbility }

/// <summary>One curated hit of a skill: a damage <see cref="Type"/> (Physical/Magic/True),
/// the BIN <see cref="Calc"/> to evaluate for its single-target raw number, and how many
/// times it lands on one target when the combo is landed fully (<see cref="Count"/>).
///
/// PERCENT-OF-HP hits (Vayne W Silver Bolts, Brand P Blaze, …): make the hit a %HP hit by naming
/// the BIN DataValue that holds the percent in <see cref="HpPercentDataValue"/> (e.g.
/// "MaxHealthRatio", "PercentHealthDamage"), and set <see cref="HpBasis"/> if it is not %-max-HP.
/// The fraction is then resolved LIVE per skill rank from that DataValue (see
/// <see cref="SkillDamage.ResolveHpPercent"/>), so it tracks the current patch and every rank —
/// NOT a baked literal, honoring the "patch-dependent values are always a dynamic BIN lookup"
/// Hard Rule.
///
/// Some %HP fractions (Skarner P Essence Sting) are not a flat <c>DataValues</c> array entry but
/// live only inside a <c>GameCalculation</c> formula tree (e.g. a <c>ByCharLevelInterpolation</c>
/// combined with an <c>mMultiplier</c>) — <see cref="HpPercentDataValue"/> cannot reach those since
/// it only indexes <c>DataValues</c>. For that case, name the calculation in
/// <see cref="HpPercentCalc"/> instead: it is evaluated with the full formula engine (see
/// <see cref="SkillDamage.ResolveHpPercentCalc"/>, backed by <see cref="FormulaInterpreter.Evaluate"/>)
/// and the RESULT is still treated as a %maxHP fraction (multiplied by the defender's max HP per
/// <see cref="HpBasis"/>), not as literal flat damage. <see cref="HpPercentDataValue"/> is tried
/// first, then <see cref="HpPercentCalc"/>, then the literal fallback below.
///
/// <see cref="HpPercent"/> is a literal fraction kept ONLY as a last-resort fallback
/// for a value genuinely absent from the BIN (neither Vayne nor Brand needs it). A hit with
/// none of these fields is a plain flat/ratio hit resolved from <see cref="Calc"/> exactly as
/// before — the fields are fully backward-compatible.
///
/// <para><b>M21 tag taxonomy</b> (<see cref="Tags"/>, per-hit; <see cref="SkillDamageDb.GetSlotTags"/>,
/// per-slot — see docs/modules/M21_SKILL_TAG_TAXONOMY.md for the full spec). Eight fixed tags,
/// PURE METADATA (curation/classification only — nothing in <c>DamageEngine</c>/<c>ComboRunner</c>
/// reads them yet; do not assume a tagged hit behaves differently from an untagged one):
/// <list type="bullet">
/// <item><description><c>PercentMaxHp</c> — hit deals a % of the target's MAX HP (<see cref="HpBasis.Max"/>
/// hits).</description></item>
/// <item><description><c>PercentMissingHp</c> — hit scales off MISSING HP (<see cref="HpBasis.Missing"/>
/// hits, including a Garen-R-style execute-scaling term).</description></item>
/// <item><description><c>Zone</c> — a persistent ground-targeted zone/DoT the target can walk out of
/// (duration-scaled hits, see <see cref="IsDurationScaled"/>, or another ground-zone skill).</description></item>
/// <item><description><c>BurstCeiling</c> — SLOT-LEVEL ONLY (<see cref="SkillDamageDb.GetSlotTags"/>,
/// never a per-hit <see cref="Tags"/> entry): at most 1-2 slots per champion, marking the skill(s) that
/// represent that champion's single highest realistic burst-damage contribution (usually the
/// ultimate).</description></item>
/// <item><description><c>MagicResistShred</c> — the skill reduces the target's magic resist, flat or
/// %, cited to a real MR-REDUCTION effect (not merely a magic-damage hit).</description></item>
/// <item><description><c>ArmorShred</c> — same as <c>MagicResistShred</c>, for armor.</description></item>
/// <item><description><c>Stacking</c> — the hit/effect builds up over repeated applications rather than
/// dealing one flat number.</description></item>
/// <item><description><c>Execute</c> — a hard-threshold execute/finisher condition, distinct from a
/// smooth <c>PercentMissingHp</c> scaling.</description></item>
/// </list>
/// <b>MagicResistShred</b>/<b>ArmorShred</b> are metadata-only for now: <see cref="Damage.DefenderStat"/>
/// already has <c>ArmorReduction</c>/<c>MrReduction</c> fields (from the T1 penetration work), but no
/// curated skill data feeds them yet — wiring an actual shred effect into those fields from a tagged
/// hit is out of scope here and is the natural next backlog step, not done in this pass.</para></summary>
public sealed class SkillHit
{
    [JsonPropertyName("type")]
    public HitDamageType Type { get; init; }

    /// <summary>(loop 497) A SECOND damage type this hit is split EQUALLY with — Yone's Spirit Cleave
    /// "deals equal parts physical and magic damage", and a hit typed wholly as one of them is
    /// mitigated by the wrong resistance for half its damage.
    ///
    /// <para>When set, the hit is emitted TWICE at half its resolved value, once as
    /// <see cref="Type"/> and once as this. Half is not a curated constant: the field says the damage
    /// is split between two types, and "equal parts" is what that means. A champion whose split is
    /// not even would need a different field, not a number written into a data file — and would be a
    /// mechanic change worth re-reading the tooltip for anyway.</para>
    ///
    /// <para>Distinct from the true-damage CONVERSION documented on Camille Q, which is level-scaled
    /// and applies to the whole attack rather than to one curated hit.</para></summary>
    [JsonPropertyName("splitType")]
    public HitDamageType? SplitType { get; init; }

    [JsonPropertyName("calc")]
    public string Calc { get; init; } = string.Empty;

    /// <summary>(M22 multi-form) Optional BIN spell leaf-name whose <c>mSpellCalculations</c> holds
    /// this hit's <see cref="Calc"/> (and any %HP/flat/per-second calc) — for a transform/stance/
    /// weapon/sub-spell ability that lives in its OWN BIN spell object rather than the node's base
    /// Q/W/E/R slot (e.g. Jayce cannon "JayceShockBlast", Gnar mega "GnarBigQ", K'Sante All-Out
    /// variant, Hwei "HweiEQ"). <see cref="ChampionBinParser.ParseExtraSpells"/> exposes these under
    /// their leaf name, so when this is set the engine resolves the calc against
    /// <c>champion.Skills[BinSpell]</c> instead of the curated slot key. Null/omitted (default) =
    /// resolve against the slot key exactly as before (fully backward-compatible). NOTE (Phase 1):
    /// the evaluation RANK still follows the generic rank fallback for a non-Q/W/E/R spell key —
    /// precise form-slot rank mapping is an M22 Phase 2 refinement.</summary>
    [JsonPropertyName("binSpell")]
    public string? BinSpell { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; } = 1;

    /// <summary>(M24 P3 DistanceScaled) BIN calc for this hit's MINIMUM-distance/charge damage — the
    /// range FLOOR (e.g. Hecarim E "MinDamage" = min charge, Fizz R "SmallSharkDamage"). Paired with
    /// <see cref="MaxCalc"/>; a node's <see cref="ComboNode.UserDistanceFraction"/> knob (0-1)
    /// interpolates between the two. See <see cref="MaxCalc"/> for the resolved-anchor semantics.</summary>
    [JsonPropertyName("minCalc")]
    public string? MinCalc { get; init; }

    /// <summary>(M24 P3 DistanceScaled) BIN calc for this hit's MAXIMUM-distance/charge damage — the
    /// range CEILING (e.g. Hecarim E "MaxDamage" = full charge, Fizz R "BigSharkDamage"). A hit is
    /// DISTANCE-SCALED iff BOTH <see cref="MinCalc"/> and this are set; the uncertainty RANGE is then
    /// [MinCalc, MaxCalc]. <see cref="Calc"/> stays the RESOLVED ANCHOR (what an UNSET knob shows) and
    /// equals whichever end the champion was already curated at — Hecarim E kept "MaxDamage" (full
    /// charge), Fizz R / Nidalee Q kept their conservative min end — so migrating a hit to
    /// distance-scaled changes NO resolved number (value identity / zero regression); it only ADDS the
    /// opposite range end. Null/omitted (default) = not distance-scaled; the hit resolves its single
    /// <see cref="Calc"/> exactly as before (fully backward-compatible).</summary>
    [JsonPropertyName("maxCalc")]
    public string? MaxCalc { get; init; }

    /// <summary>True when this hit is distance/charge-scaled (both <see cref="MinCalc"/> and
    /// <see cref="MaxCalc"/> are set).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDistanceScaled => !string.IsNullOrEmpty(MinCalc) && !string.IsNullOrEmpty(MaxCalc);

    /// <summary>(M25 §11.G) The condition gating a CONDITIONAL-BONUS hit — the
    /// <see cref="Overlay.Core.Combo.ConditionType"/> name ("ResourceGte", "VsIsolated", "VsDebuffed",
    /// "MeleeRangeCast", "HpBelow", "HitsWall") whose satisfaction switches this hit from its baseline
    /// <see cref="Calc"/> (the conservative UNMET value = RangeMin anchor, P2) to <see cref="MetCalc"/>
    /// (the MET value = RangeMax). AutoResolvable conditions (own resource) read live state;
    /// UserAssumed ones (enemy/positional/terrain state) read the node's
    /// <see cref="ComboNode.UserConditionMet"/> knob. Distinct from an EXECUTE: this INCREASES damage,
    /// never insta-kills. Null/omitted (default) = not conditional, resolves <see cref="Calc"/> exactly
    /// as before (fully backward-compatible).
    ///
    /// <para>(M28 — binary "최대 데미지" checkbox shape, e.g. K'Sante R wall-hit) <see cref="Calc"/>
    /// may be left EMPTY for a hit whose UNMET state is "doesn't happen at all" rather than "a real
    /// lesser value" — <see cref="ComboRunner"/>'s conditional-hit resolver tries to evaluate the
    /// empty calc name, finds no such BIN calc, and drops the hit (0 contribution), which is the
    /// correct P2 floor for a pure on/off assumption. This is DIFFERENT from the original M25 shape
    /// (a real baseline vs. an amplified MET value, e.g. Kha'Zix Q isolated) — both are valid uses of
    /// the same <see cref="ConditionType"/>/<see cref="MetCalc"/> mechanism, distinguished only by
    /// whether <see cref="Calc"/> names a real calc or is left unset.</para></summary>
    [JsonPropertyName("conditionType")]
    public string? ConditionType { get; init; }

    /// <summary>(M25 §11.G) Threshold for <see cref="ConditionType"/> (ResourceGte 50 = 50 fury,
    /// HpBelow 0.20 = target below 20% max HP). Ignored for conditions that carry no threshold
    /// (VsIsolated/VsDebuffed/MeleeRangeCast). Default 0.</summary>
    [JsonPropertyName("conditionValue")]
    public double ConditionValue { get; init; }

    /// <summary>(M25 §11.G) BIN calc for this hit's damage WHEN <see cref="ConditionType"/> holds — the
    /// RangeMax end (e.g. Kha'Zix Q "IsoDamage" isolated value, Renekton empowered value). Paired with
    /// <see cref="ConditionType"/>; a hit is CONDITIONAL iff both are set.</summary>
    [JsonPropertyName("metCalc")]
    public string? MetCalc { get; init; }

    /// <summary>(loop 479) The MET value as a %HP FRACTION rather than a flat number — the same role
    /// <see cref="MetCalc"/> plays, for a condition whose empowered half is a percentage of the
    /// target's health (Jhin's fourth shot: 15/20/25% of MISSING health; Udyr's Awakened claw: a
    /// %maxHP lightning bonus). Read against this hit's <see cref="HpBasis"/> exactly like
    /// <see cref="HpPercentCalc"/>, so a Missing/Current basis becomes an execute-style term and Max
    /// becomes flat damage.
    ///
    /// <para>Needed because the conditional branch resolves its chosen calc as FLAT damage: pointing
    /// <see cref="MetCalc"/> at a fraction calc would have quietly contributed 0.15 damage instead of
    /// 15% of the target's health. Set at most ONE of the two.</para></summary>
    [JsonPropertyName("metHpPercentCalc")]
    public string? MetHpPercentCalc { get; init; }

    /// <summary>(loop 480) Name of a BIN DataValue holding a scalar by which this hit's own base
    /// damage GROWS with the target's missing health — <c>base × (1 + scalar × missingFraction)</c>,
    /// re-evaluated against the target's health at the moment the hit lands. Hwei QW (Severing Bolt)
    /// is the motivating case: BonusDamageMult 2.0-3.5 by rank, i.e. the wiki's "increased by up to
    /// 200%/…/350% based on the target's missing health".
    ///
    /// <para>Resolves to <see cref="ComboExecuteType.BaseWithMissingHpBonus"/>, the shape Kraken
    /// Slayer's Bring It Down already used from the ITEM side — this only opens it to a curated
    /// skill hit. Distinct from a %HP hit with <see cref="HpBasis"/> Missing: that ADDS a fraction of
    /// the target's absolute missing health, while this MULTIPLIES a damage number that has its own
    /// AP ratio. Curating one as the other is off by orders of magnitude in either direction.</para>
    ///
    /// <para>Null/omitted (default) = an ordinary hit. Also the shape Bel'Veth E's documented
    /// MissingHealthMult gap needs, left alone here because it would move a golden.</para></summary>
    [JsonPropertyName("missingHpBonusDataValue")]
    public string? MissingHpBonusDataValue { get; init; }

    /// <summary>True when this hit is a conditional-bonus hit (both <see cref="ConditionType"/> and
    /// <see cref="MetCalc"/> are set).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConditional => !string.IsNullOrEmpty(ConditionType)
                                && (!string.IsNullOrEmpty(MetCalc) || !string.IsNullOrEmpty(MetHpPercentCalc));

    /// <summary>(M25 §11.G) True when this hit's <see cref="Calc"/> carries a BuffCounter per-stack term
    /// (e.g. Nasus Q Siphoning-Strike stacks) that the user's stack knob
    /// (<see cref="ComboNode.UserStackCount"/>) feeds. This is purely a UI HINT so the combo editor
    /// offers a "몇 스택" input on this node; the engine already threads UserStackCount into every hit's
    /// calc (a calc with no stack term ignores it), so this flag does NOT change damage resolution.
    /// Null/false (default) = no stack input offered, fully backward-compatible.</summary>
    [JsonPropertyName("stackScaled")]
    public bool StackScaled { get; init; }

    /// <summary>(loop 484) A hit whose damage is CONSUMED IN TIERS: N stacks land the base
    /// <see cref="Calc"/> N times, raised by a bonus percentage that depends on N. Locke's Soul Nails
    /// is the motivating case and the user stated the shape outright — base, then (base x 2) x 1.2 at
    /// two nails and (base x 3) x 1.4 at three.
    ///
    /// <para>Each entry names the BIN DataValue holding the bonus for one tier ABOVE the first, in
    /// order: <c>["TwoMarkBonusPercent", "ThreeMarkBonusPercent"]</c> means index 0 applies at 2
    /// stacks and index 1 at 3, and the array's length therefore states the cap (two entries → three
    /// stacks). A value of 20 reads as 20%, matching the >=1-is-a-percentage convention
    /// <see cref="SkillDamage.ResolveHpPercent"/> already uses.</para>
    ///
    /// <para>Why a field rather than three curated numbers: the tiers ARE expressible as multipliers
    /// (1x, 2.4x, 4.2x) and writing those in would put two DERIVED constants in a data file, silently
    /// wrong the first time a balance patch moves either percentage. Every number here is a live
    /// lookup; only the stack COUNT is structural, and that comes from the array's own length.</para>
    ///
    /// <para>The count itself is the node's <see cref="ComboNode.UserStackCount"/> knob, clamped to
    /// [1, entries+1]. Unset (0) resolves as ONE stack — the plain base calc, which is exactly what
    /// this hit resolved to before the field existed.</para></summary>
    [JsonPropertyName("stackTierBonusDataValues")]
    public string[]? StackTierBonusDataValues { get; init; }

    /// <summary>True when this hit consumes stacks in tiers (see
    /// <see cref="StackTierBonusDataValues"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStackTiered => StackTierBonusDataValues is { Length: > 0 };

    /// <summary>The highest stack count this hit models — one more than the number of bonus tiers, so
    /// the editor's knob can bound itself without a curated constant. 0 when not stack-tiered.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int MaxStackTier => IsStackTiered ? StackTierBonusDataValues!.Length + 1 : 0;

    /// <summary>Optional taxonomy tags classifying THIS hit — see the M21 tag-taxonomy doc comment
    /// above <see cref="SkillHit"/> for the full list and definitions. Pure metadata: nothing in
    /// <see cref="ComboRunner"/>/<c>DamageEngine</c> reads this today. Null/omitted (default) = no
    /// tags, and every pre-existing curated file with no "tags" key parses identically to before
    /// this field was added (fully backward-compatible).</summary>
    [JsonPropertyName("tags")]
    public string[]? Tags { get; init; }

    /// <summary>Name of the BIN DataValue holding this hit's percent-of-HP value, resolved live
    /// per rank (Vayne W "MaxHealthRatio", Brand P "PercentHealthDamage"). When set, the hit is a
    /// %HP hit and <see cref="Calc"/> is ignored. Null (default) = not a DataValue-sourced %HP hit.</summary>
    [JsonPropertyName("hpPercentDataValue")]
    public string? HpPercentDataValue { get; init; }

    /// <summary>Name of the BIN <c>GameCalculation</c> (spell calculation, evaluated via
    /// <see cref="FormulaInterpreter.Evaluate"/>) whose evaluated number is this hit's
    /// percent-of-HP FRACTION — for %HP mechanics that live inside a formula tree rather than a
    /// flat <see cref="HpPercentDataValue"/> array entry (Skarner P "PercentHealthDamage" =
    /// <c>ByCharLevelInterpolation(5.0→9.0) × mMultiplier(0.01)</c>). Tried only when
    /// <see cref="HpPercentDataValue"/> is unset or fails to resolve. Null (default) = not a
    /// calc-sourced %HP hit.</summary>
    [JsonPropertyName("hpPercentCalc")]
    public string? HpPercentCalc { get; init; }

    /// <summary>Last-resort LITERAL percent-of-HP fraction (0.09 = 9%), used only when neither
    /// <see cref="HpPercentDataValue"/> nor <see cref="HpPercentCalc"/> is set or resolves. Prefer
    /// the BIN-sourced fields; a non-zero literal here is a curated constant a future BIN pass
    /// should replace. Default 0 = no literal fallback.</summary>
    [JsonPropertyName("hpPercent")]
    public double HpPercent { get; init; }

    /// <summary>Which HP value a %HP hit scales on: the target's max HP (default), its live
    /// current HP, or its missing HP. Only read when the hit is a %HP hit (has
    /// <see cref="HpPercentDataValue"/>, <see cref="HpPercentCalc"/>, or a non-zero
    /// <see cref="HpPercent"/>).</summary>
    [JsonPropertyName("hpBasis")]
    public HpBasis HpBasis { get; init; } = HpBasis.Max;

    /// <summary>Name of a BIN DataValue holding this hit's FLAT (non-%HP) damage number,
    /// resolved live per rank via <see cref="SkillDamage.ComputeFlatDataValue"/> — for a hit
    /// whose real number is a plain rank-indexed <c>SkillData.DataValues</c> entry with NO
    /// separately-resolvable <see cref="Calc"/> of its own. Motivating case: Garen R "Demacian
    /// Justice"'s flat base term (150/250/350) — the BIN's only GameCalculation for it bundles
    /// that DataValue together with a missing-health term that itself references a BIN stat id
    /// (15) this codebase's stat resolvers do not map (no live "target's missing health" stat
    /// exists in an ATTACKER-side resolver), so the combined calc is not resolvable here; the
    /// flat base and the missing-health scaling are instead curated as two independent hits
    /// (this DataValue-sourced flat one, plus a separate %HP hit using
    /// <see cref="HpPercentDataValue"/>/<see cref="HpBasis.Missing"/> for the real, dynamic
    /// "ExecuteDamage" DataValue) that DamageEngine sums — mathematically equivalent to the
    /// BIN's own base-plus-term sum-of-subparts shape. Tried only when <see cref="Calc"/> is
    /// empty/unresolvable. Null (default) = not used.</summary>
    [JsonPropertyName("flatDataValue")]
    public string? FlatDataValue { get; init; }

    /// <summary>(loop 471) BIN DataValue holding a per-cast RAMP fraction, for an ability whose
    /// later casts deal more than its first — Aatrox Q's <c>QRampBonus</c> (0.25), where the second
    /// cast deals 125% and the third 150%.
    ///
    /// <para>This needs its own field because the ramp is applied by SCRIPT, not by a calc: AatroxQ's
    /// mSpellCalculations holds only QDamage/QEdgeDamage/QMinionDamage, and there is no Q2/Q3 spell
    /// object to point <see cref="BinSpell"/> at. That is the Zac-W shape
    /// (tools/sweep_unreferenced_damage.py) — real damage a calc-name-driven curator cannot see.</para>
    ///
    /// <para>Resolved value = <c>calc × (1 + <see cref="RampSteps"/> × DataValue)</c>, the DataValue
    /// read live at the node's rank. Naming the DataValue instead of writing 1.25 is the Hard Rule:
    /// a patch that retunes the ramp moves this number without anyone editing curation.</para></summary>
    [JsonPropertyName("rampDataValue")]
    public string? RampDataValue { get; init; }

    /// <summary>(loop 471) How many ramp steps past the first cast this one is — 1 for a second cast,
    /// 2 for a third. Ignored unless <see cref="RampDataValue"/> is set. Additive rather than
    /// compounding, because that is what the game does: Aatrox's third cast is 150%, not 125% twice
    /// over (wiki: 10/25/40/55/70 first, 12.5/31.25/50/68.75/87.5 second, 15/37.5/60/82.5/105 third).</summary>
    [JsonPropertyName("rampSteps")]
    public int RampSteps { get; init; }

    /// <summary>True when this hit carries a per-cast ramp multiplier.</summary>
    [JsonIgnore]
    public bool IsRamped => !string.IsNullOrEmpty(RampDataValue) && RampSteps > 0;

    /// <summary>Curated fraction of the target's MAX HP this hit deals PER SECOND (0.06 = 6%/s), for
    /// a DURATION-SCALED hit — an escapable persistent zone/DoT the target can walk out of before
    /// taking full damage (e.g. Malzahar W "Null Zone"), which this project's prior convention was to
    /// OMIT ENTIRELY rather than assume full duration (P2). Paired with
    /// <see cref="MaxDurationSeconds"/>, this instead lets the combo editor's per-node "적중시간"
    /// (hit duration) control record how many seconds the user says the target was actually exposed;
    /// <see cref="ComboRunner"/> then computes PARTIAL damage = this fraction × defender max HP
    /// × min(<see cref="ComboNode.UserHitDurationSeconds"/>, <see cref="MaxDurationSeconds"/>). Unset
    /// node duration (null, the default) → 0 damage, the same honest default as full omission.
    ///
    /// This is a LITERAL, wiki-cited constant, not a live BIN DataValue/calc lookup — the motivating
    /// case (Null Zone) has no readable BIN calc name for its own per-tick damage (only an
    /// unlabeled hash), so the value is curated from a live wiki fetch instead, the same
    /// already-established convention as Kraken Slayer/Garen R's wiki-cited literals elsewhere in
    /// this codebase. Always interpreted as a %-of-MAX-HP rate (this hit type does not consult
    /// <see cref="HpBasis"/> — every currently known escapable zone/DoT mechanic scales on max HP,
    /// and duration-scaling %current/%missing HP would need a live-HP re-evaluation shape this
    /// codebase's <see cref="ComboExecuteType"/> doesn't have yet; not added speculatively). Null
    /// (default) = not a duration-scaled hit.</summary>
    [JsonPropertyName("perSecondHpPercent")]
    public double? PerSecondHpPercent { get; init; }

    /// <summary>Name of a BIN <c>mSpellCalculations</c> GameCalculation whose evaluated number is
    /// this hit's FLAT damage PER SECOND — the flat-damage sibling of <see cref="PerSecondHpPercent"/>
    /// for a persistent, escapable damage source whose per-tick number is a normal base+ratio calc
    /// (NOT a %HP fraction), e.g. Heimerdinger Q's H-28G turret. Resolved LIVE from the BIN per rank
    /// (so it tracks the current patch AND the attacker's AP, honoring the "patch-dependent values are
    /// always a dynamic BIN lookup" Hard Rule), then multiplied by the user-entered exposure seconds
    /// (<see cref="ComboNode.UserHitDurationSeconds"/>, clamped to <see cref="MaxDurationSeconds"/>) in
    /// <see cref="ComboRunner.TryBuildHitShape"/>. Tried in the duration-scaled branch BEFORE
    /// <see cref="PerSecondHpPercent"/>. Null (default) = not a flat per-second DoT hit. Paired with
    /// <see cref="MaxDurationSeconds"/> just like <see cref="PerSecondHpPercent"/> — both are required
    /// for <see cref="IsDurationScaled"/> to be true.</summary>
    [JsonPropertyName("perSecondCalc")]
    public string? PerSecondCalc { get; init; }

    /// <summary>(§27 Nasus R) Name of a BIN <c>mSpellCalculations</c>/DataValue giving this hit's
    /// %-of-MAX-HP rate for a persistent DoT — the BIN-SOURCED, rank-scaled sibling of the literal
    /// <see cref="PerSecondHpPercent"/>. Motivating case: Nasus R (Fury of the Sands)'s
    /// <c>AOEDamagePercent</c> (r1..3 = 3/4/5% of the target's MAX HP PER SECOND), which a literal
    /// double can't rank-scale. Reconciled against the wiki: 5%/s at a 0.5s tick = 2.5%/tick and over
    /// the 15s duration = 75% total, both wiki-confirmed, so the DataValue is a PER-SECOND rate.
    /// Resolved LIVE per rank via <see cref="SkillDamage.ResolveHpPercent"/> (Hard Rule: patch-dependent
    /// values are always a dynamic BIN lookup), then scaled by the user-entered exposure seconds like
    /// <see cref="PerSecondHpPercent"/>. Tried in the duration-scaled branch BEFORE the literal. Paired
    /// with <see cref="MaxDurationSeconds"/>, whose ceiling makes the total naturally cap at
    /// rate×maxDuration (= 45-75% maxHP). Null (default) = not a BIN-sourced %HP DoT.</summary>
    [JsonPropertyName("perSecondHpPercentDataValue")]
    public string? PerSecondHpPercentDataValue { get; init; }

    /// <summary>(§27 Nasus R) Calc-sourced sibling of <see cref="PerSecondHpPercentDataValue"/>: the name
    /// of a BIN <c>mSpellCalculations</c> GameCalculation whose evaluated value is this DoT's %-of-MAX-HP
    /// rate (e.g. Nasus R's <c>AoeDamagePercent</c> = rank% + a small AP term). Resolved via
    /// <see cref="SkillDamage.ResolveHpPercentCalc"/> (which normalizes a ≥1 output as a percent).
    /// Tried in the duration-scaled branch after <see cref="PerSecondHpPercentDataValue"/> and before
    /// the literal <see cref="PerSecondHpPercent"/>. Null (default) = not a calc-sourced %HP DoT.</summary>
    [JsonPropertyName("perSecondHpPercentCalc")]
    public string? PerSecondHpPercentCalc { get; init; }

    /// <summary>(M22 Phase 3) Name of a BIN calc giving this hit's PER-ATTACK (per-hit) flat damage for
    /// a SUMMON/pet skill whose total output depends on how many times the summon hits — Annie R
    /// (Tibbers' auto-attacks), Ivern Daisy, etc. Resolved LIVE from the BIN (tracks patch + AP), then
    /// multiplied by the node's user-entered <see cref="ComboNode.UserAttackCount"/> in
    /// <see cref="ComboRunner.TryBuildHitShape"/>. Null (default) = not a per-attack summon hit. A hit
    /// with this set is neither %HP nor duration-scaled; unset/0 attack count → 0 damage (honest P2
    /// default, a summon's real output is player-uptime-dependent, never a fixed cast number).</summary>
    [JsonPropertyName("perAttackCalc")]
    public string? PerAttackCalc { get; init; }

    /// <summary>(M22 Phase 4) BIN calc to use for this hit INSTEAD of <see cref="Calc"/> while the combo
    /// is in the K'Sante All-Out stance (a preceding R node set it — see <see cref="ComboRunner"/>'s
    /// node loop). Null (default) = no All-Out variant, resolve normally regardless of stance. Only the
    /// plain-calc path honors this today (K'Sante's All-Out is a plain-calc swap).</summary>
    [JsonPropertyName("allOutCalc")]
    public string? AllOutCalc { get; init; }

    /// <summary>(M22 Phase 4) BIN spell leaf-name to resolve <see cref="AllOutCalc"/> against while in
    /// All-Out, when the empowered value lives in a different spell object than the base hit. Falls
    /// back to <see cref="BinSpell"/>/the slot when null. Only read when <see cref="AllOutCalc"/> is set
    /// and the stance is active.</summary>
    [JsonPropertyName("allOutBinSpell")]
    public string? AllOutBinSpell { get; init; }

    /// <summary>(§19, GOLDEN_02 §7) True = this hit EXISTS ONLY while the combo is in the K'Sante
    /// All-Out stance (a preceding R node — see <see cref="ComboRunner"/>'s node loop) and is dropped
    /// entirely outside it; distinct from <see cref="AllOutCalc"/>, which merely SWAPS an existing
    /// hit's calc. The motivating case is the All-Out W charge rider below (a whole extra TRUE hit that
    /// the base-form W does not have). Default false = the hit fires regardless of stance, fully
    /// backward-compatible for every pre-existing hit.</summary>
    [JsonPropertyName("allOutOnly")]
    public bool AllOutOnly { get; init; }

    /// <summary>(§16 P-round) The inverse of <see cref="AllOutOnly"/>: true = this hit fires ONLY in the
    /// BASE (non-All-Out) stance and is dropped once a preceding R put the combo in All-Out. Motivating
    /// case: K'Sante's base-form P mark (flat 12 + level-scaled %maxHP) which All-Out REPLACES with a
    /// different rider (the <see cref="AllOutOnly"/> hit), so the two must be mutually exclusive by
    /// stance. Default false = fires regardless of stance, fully backward-compatible.</summary>
    [JsonPropertyName("baseStanceOnly")]
    public bool BaseStanceOnly { get; init; }

    /// <summary>(§19, GOLDEN_02 §7 — K'Sante All-Out W) BIN DataValue name holding the charge ramp's
    /// MINIMUM fraction (K'Sante W <c>RDamageIncreaseMin</c> = 0.10). Paired with
    /// <see cref="ChargeMaxDataValue"/>/<see cref="ChargeTimeMinDataValue"/>/
    /// <see cref="ChargeTimeFullDataValue"/>; when all four are set the hit is CHARGE-SCALED
    /// (<see cref="IsChargeScaled"/>) and its damage is <c>f × baseRaw</c> where <c>baseRaw</c> is
    /// this hit's own <see cref="FlatDataValue"/> + <see cref="HpPercentCalc"/> (%maxHP) terms and
    /// <c>f</c> ramps this Min→<see cref="ChargeMaxDataValue"/> by the node's charge knob
    /// (<see cref="ComboNode.UserDistanceFraction"/>). Every value is a live BIN lookup (Hard Rule —
    /// no baked literals). Null (default) = not charge-scaled.</summary>
    [JsonPropertyName("chargeMinDataValue")]
    public string? ChargeMinDataValue { get; init; }

    /// <summary>(§19) BIN DataValue name holding the charge ramp's MAXIMUM fraction (K'Sante W
    /// <c>RDamageIncreaseMax</c> = 0.80). See <see cref="ChargeMinDataValue"/>.</summary>
    [JsonPropertyName("chargeMaxDataValue")]
    public string? ChargeMaxDataValue { get; init; }

    /// <summary>(§19) BIN DataValue name holding the charge time (normalized 0-1 fraction of the
    /// ability's max charge duration) at/below which the ramp stays at its MINIMUM (K'Sante W
    /// <c>MinChargeTime</c> = 0.4). See <see cref="ChargeMinDataValue"/>.</summary>
    [JsonPropertyName("chargeTimeMinDataValue")]
    public string? ChargeTimeMinDataValue { get; init; }

    /// <summary>(§19) BIN DataValue name holding the charge time (normalized 0-1) at/above which the
    /// ramp is CAPPED at its MAXIMUM (K'Sante W <c>TimeToFullCharge</c> = 0.9). See
    /// <see cref="ChargeMinDataValue"/>.</summary>
    [JsonPropertyName("chargeTimeFullDataValue")]
    public string? ChargeTimeFullDataValue { get; init; }

    /// <summary>True when this hit is charge-scaled (all four charge DataValue names are set — the
    /// four are meaningless individually, so no separate boolean flag, matching
    /// <see cref="IsDistanceScaled"/>/<see cref="IsDurationScaled"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsChargeScaled =>
        !string.IsNullOrEmpty(ChargeMinDataValue) && !string.IsNullOrEmpty(ChargeMaxDataValue)
        && !string.IsNullOrEmpty(ChargeTimeMinDataValue) && !string.IsNullOrEmpty(ChargeTimeFullDataValue);

    /// <summary>The ability's real maximum zone/DoT duration in seconds (wiki-cited) — the clamp
    /// ceiling for a user-entered "적중시간" on a duration-scaled hit (see
    /// <see cref="PerSecondHpPercent"/>). Null (default) = not a duration-scaled hit.</summary>
    [JsonPropertyName("maxDurationSeconds")]
    public double? MaxDurationSeconds { get; init; }

    /// <summary>True when this hit is a duration-scaled, user-controlled partial-damage hit (see
    /// <see cref="PerSecondHpPercent"/>) rather than an assumed-landed single hit. Inferred from BOTH
    /// <see cref="PerSecondHpPercent"/> and <see cref="MaxDurationSeconds"/> being set — the two
    /// numbers are meaningless without each other, so a third, separately-settable boolean flag that
    /// could drift out of sync with them was deliberately not added (YAGNI). Computed, not itself
    /// part of the curated JSON schema. A FLAT per-second DoT (<see cref="PerSecondCalc"/> set,
    /// paired with <see cref="MaxDurationSeconds"/>) is equally duration-scaled — either rate source
    /// plus a max duration qualifies.</summary>
    [JsonIgnore]
    public bool IsDurationScaled =>
        (PerSecondHpPercent is > 0 || !string.IsNullOrEmpty(PerSecondCalc)
         || !string.IsNullOrEmpty(PerSecondHpPercentDataValue)
         || !string.IsNullOrEmpty(PerSecondHpPercentCalc)) && MaxDurationSeconds is > 0;

    /// <summary>(M27) True when this curated hit can crit — abilities can't crit by default (only
    /// auto-attacks, via <see cref="Overlay.Core.Combo.ComboEngine"/>'s own AA-only rule); this ORs in
    /// on top of that rule for a specific curated ability hit (e.g. Garen E). Paired with
    /// <see cref="CritDamageScalar"/>. Default false = no behavior change for any pre-existing hit.</summary>
    [JsonPropertyName("canCrit")]
    public bool CanCrit { get; init; }

    /// <summary>(M27) Fraction of a FULL crit's bonus this hit deals when it crits — 1.0 (default) is a
    /// full/normal crit, identical to an auto-attack's; e.g. Garen E = 0.40 ("E crits for 40% of a
    /// standard crit's bonus" — measured +30% at base 1.75 crit multiplier, 0.30/0.75 = 0.40). Only
    /// meaningful when <see cref="CanCrit"/> is true. Default 1.0 keeps every pre-existing crit-capable
    /// hit (AA) byte-identical (see DamageEngine.CritFactor's backward-compat proof).</summary>
    [JsonPropertyName("critDamageScalar")]
    public double CritDamageScalar { get; init; } = 1.0;

    /// <summary>(§27 (B), dynamic low-HP on-hit) When set (0-1), this hit is DYNAMICALLY gated on the
    /// target's RUNNING hypothetical HP: it contributes damage only once the combo's own damage has
    /// depleted the (per-track) HP to/below this fraction of max HP — a low-HP-only proc that turns on
    /// partway through the combo (e.g. Ekko W's built-in passive against enemies ≤30% HP → 0.30).
    /// Unlike the <see cref="ConditionType.HpBelow"/> node condition (a coarse user-ASSUMED knob for the
    /// enemy's unobservable live HP), this is evaluated LIVE per damage track against the combo's own
    /// depleting HP, so the kill-angle reflects the proc turning on mid-combo. Threaded to
    /// <c>Damage.ComboNode.HpBelowGateFraction</c>. Null (default) = ungated.</summary>
    [JsonPropertyName("hpBelowGate")]
    public double? HpBelowGate { get; init; }

    /// <summary>(GOLDEN #3 round 3 — docs/reports/golden/GOLDEN_03_GAREN.md §5) When set, this
    /// hit's resolved strike COUNT is <see cref="Count"/> + floor(countedAS / this value) instead of
    /// the static <see cref="Count"/> alone (INCLUSIVE boundary — exactly hitting a multiple of this
    /// step grants the extra strike). <c>countedAS</c> = the active player's ITEM-sourced bonus
    /// attack speed (Data Dragon <see cref="ChampionDb.ItemStats.AttackSpeedPercent"/>, summed across
    /// built items) PLUS the champion's own level-GROWTH attack speed (Data Dragon
    /// <see cref="ChampionDb.ChampionStatsPerLevel.AttackSpeed"/>, via the same per-level curve
    /// <c>ChampionDb.LevelGrowth.Stat</c> already applies to HP/AD/Armor/MR) — expressed in WHOLE
    /// PERCENT POINTS (15.0 = +15%) to match this field's own units.
    ///
    /// Deliberately EXCLUDES rune/stat-shard attack speed: the Live Client API's live panel only
    /// reports a single TOTAL bonus-AS number that bundles item+growth+rune/shard together with no
    /// way to decompose it, and using that total directly regresses at low levels — a common Attack
    /// Speed shard (+10%) can push the panel's total over a strike-count boundary the item+growth
    /// alone would not cross (GOLDEN_03_GAREN.md §5's own L1 case: panel showed 25% including a 10%
    /// shard, item+growth alone was only 15%, and the observed strike count proved the shard's 10%
    /// must NOT count). Garen E (Judgment) = 7 base + 1 strike per 25 percentage points of countedAS.
    /// Null (default) = the static <see cref="Count"/> is used as-is, fully backward-compatible for
    /// every other curated hit.</summary>
    [JsonPropertyName("attackSpeedStrikeStep")]
    public double? AttackSpeedStrikeStep { get; init; }
}

/// <summary>Curated per-hit damage type. Deliberately this module's own small enum (parsed
/// from the JSON) rather than a reuse of <see cref="ChampionDb.DamageType"/> (which has a
/// MIXED member that is not a valid single-hit type).</summary>
public enum HitDamageType { Physical, Magic, True }

/// <summary>The HP value a percent-of-HP <see cref="SkillHit"/> scales on. <see cref="Max"/> is a
/// flat-per-cast number (percent × the target's max HP, constant during a combo); <see cref="Current"/>
/// and <see cref="Missing"/> map to the M05 engine's CurrentHp/MissingHp execute types, which
/// re-evaluate against the target's live HP as the combo ticks.</summary>
public enum HpBasis { Max, Current, Missing }
