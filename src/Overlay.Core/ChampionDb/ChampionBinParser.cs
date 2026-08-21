using System.Text.Json;

namespace Overlay.Core.ChampionDb;

/// <summary>
/// Parses CommunityDragon's <c>{champion}.bin.json</c> (see <see cref="CommunityDragonClient"/>)
/// to extract, per Q/W/E/R skill slot, the raw <c>DataValues</c> arrays and
/// <c>mSpellCalculations</c> formula trees needed by <see cref="FormulaInterpreter"/>.
/// Pure parsing — no network/file I/O beyond the JSON string handed in, mirroring
/// <see cref="DDragonParser"/>'s separation of concerns.
/// </summary>
public static class ChampionBinParser
{
    private static readonly string[] SlotKeys = { "Q", "W", "E", "R" };

    /// <summary>Returns, per slot ("Q"/"W"/"E"/"R"), the skill's DataValues and
    /// SpellCalculations. Slots the BIN doesn't define for this champion are omitted —
    /// callers merge only the slots present.</summary>
    public static Dictionary<string, BinSkillData> ParseChampion(string championId, string binJson)
    {
        using var doc = JsonDocument.Parse(binJson);
        var root = doc.RootElement;

        var characterRecord = FindByType(root, "CharacterRecord")
            ?? throw new InvalidDataException($"No CharacterRecord found in {championId}.bin.json");
        if (!characterRecord.TryGetProperty("spellNames", out var spellNamesEl))
            throw new InvalidDataException($"CharacterRecord for {championId} has no spellNames");

        var result = new Dictionary<string, BinSkillData>();
        var i = 0;
        foreach (var spellNameEl in spellNamesEl.EnumerateArray())
        {
            if (i >= SlotKeys.Length) break;
            var slot = SlotKeys[i++];
            var relPath = spellNameEl.GetString();
            if (string.IsNullOrEmpty(relPath)) continue;

            var spellKey = $"Characters/{championId}/Spells/{relPath}";
            if (!root.TryGetProperty(spellKey, out var spellObj)) continue;
            if (!spellObj.TryGetProperty("mSpell", out var mSpell)) continue;

            result[slot] = new BinSkillData
            {
                DataValues = ParseDataValues(mSpell),
                SpellCalculations = ParseSpellCalculations(mSpell),
                EffectAmounts = ParseEffectAmounts(mSpell),
            };
        }

        TryParsePassive(root, characterRecord, result);
        // Collect the canonical spell paths (from spellNames and passive) so ParseExtraSpells
        // can skip their direct children but still expose other form/transform spells.
        var canonicalSpellPaths = new HashSet<string>();
        foreach (var spellNameEl in spellNamesEl.EnumerateArray())
        {
            var relPath = spellNameEl.GetString();
            if (!string.IsNullOrEmpty(relPath))
                canonicalSpellPaths.Add(relPath);
        }
        if (characterRecord.TryGetProperty("mCharacterPassiveSpell", out var passiveEl)
            && passiveEl.ValueKind == JsonValueKind.String)
        {
            var passiveKey = passiveEl.GetString();
            if (!string.IsNullOrEmpty(passiveKey) && passiveKey.StartsWith($"Characters/{championId}/Spells/"))
            {
                canonicalSpellPaths.Add(passiveKey[($"Characters/{championId}/Spells/".Length)..]);
            }
        }
        ParseExtraSpells(root, championId, result, canonicalSpellPaths);
        return result;
    }

    /// <summary>
    /// (M22 multi-form support) Parses EVERY other <c>Characters/{Id}/Spells/{X}/mSpell</c> object
    /// into <paramref name="result"/> keyed by the spell leaf-name <c>X</c> (e.g. "JayceShockBlast",
    /// "GnarBigQ", "RekSaiQBurrowed", "HweiEQ"), so a curated slot can resolve its calc against a
    /// transform/stance/weapon/sub-spell object via <c>SkillHit.BinSpell</c> — the numbers Riot puts
    /// in these objects are otherwise unreachable (the CharacterRecord maps only spellNames[0..3] to
    /// Q/W/E/R). Additive: the canonical Q/W/E/R/P keys are already set and never overwritten (leaf
    /// names never equal a single-letter slot key). Only spells that actually carry
    /// <c>mSpellCalculations</c> are added — a pure-utility spell with no calc is useless for combo
    /// damage and is skipped to avoid bloating the per-champion skill map.
    /// </summary>
    private static void ParseExtraSpells(JsonElement root, string championId, Dictionary<string, BinSkillData> result,
        ISet<string> canonicalSpellPaths)
    {
        var prefix = $"Characters/{championId}/Spells/";
        foreach (var prop in root.EnumerateObject())
        {
            bool underSpells = prop.Name.StartsWith(prefix, StringComparison.Ordinal);
            // (M22 Phase 5) Some multi-form champions store form/weapon spell objects NOT under the
            // readable "Characters/{id}/Spells/..." path but at a top-level HASHED key (e.g. Aphelios'
            // 5 per-weapon Q spells live at "{9501e989}" etc.). Expose those too, keyed by the hash, so
            // a curated slot can reach them via SkillHit.BinSpell. Bounded by the {hex} key shape AND
            // the mSpell/calc filter below, so unrelated hashed objects are not pulled in.
            bool hashedSpell = !underSpells && prop.Name.Length > 2
                && prop.Name[0] == '{' && prop.Name[^1] == '}';
            if (!underSpells && !hashedSpell) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("mSpell", out var mSpell)) continue;

            var leaf = underSpells ? prop.Name[(prop.Name.LastIndexOf('/') + 1)..] : prop.Name;
            if (string.IsNullOrEmpty(leaf) || result.ContainsKey(leaf)) continue;

            // Hashed-key spells have no ability-wrapper parent to dedupe against — take them directly.
            if (!underSpells)
            {
                var parsedHashed = ParseSpell(mSpell);
                if (parsedHashed.SpellCalculations.Count == 0) continue;
                result[leaf] = parsedHashed;
                continue;
            }

            // Skip direct children of canonical ability objects (e.g. "Characters/Aatrox/Spells/AatroxQAbility/AatroxQ").
            // These are already in result under their canonical slot keys (Q/W/E/R/P). Identify them by checking
            // if the relative path (after "Characters/{id}/Spells/") is a direct child of a canonical spell path
            // (i.e., "relPath" has exactly one "/" and its parent matches a canonical path).
            var relPath = prop.Name[prefix.Length..];
            var slashIndex = relPath.IndexOf('/');
            if (slashIndex > 0)
            {
                var parent = relPath[..slashIndex];
                var child = relPath[(slashIndex + 1)..];
                var parentPathWithChild = $"{parent}/{child}";
                if (canonicalSpellPaths.Contains(parentPathWithChild))
                    continue; // This child spell is part of a canonical spell's ability structure
            }

            var parsed = ParseSpell(mSpell);
            if (parsed.SpellCalculations.Count == 0) continue; // no damage calc -> not useful here
            result[leaf] = parsed;
        }
    }

    /// <summary>
    /// Extracts the champion's PASSIVE spell (slot key <c>"P"</c>) in the same shape as Q/W/E/R.
    /// The <c>CharacterRecord</c> points at its passive spell via <c>mCharacterPassiveSpell</c> —
    /// a full object key into the BIN root (either a readable
    /// <c>"Characters/{id}/Spells/{name}Ability/{name}Passive"</c> path or a hashed link name),
    /// separate from the four <c>spellNames</c> ability entries. Champions whose passive carries
    /// no <c>mSpell</c> block (pure buff/utility passives) are simply omitted, so callers get a
    /// <c>"P"</c> slot only when the passive has evaluable DataValues/SpellCalculations.
    /// </summary>
    private static void TryParsePassive(
        JsonElement root, JsonElement characterRecord, Dictionary<string, BinSkillData> result)
    {
        if (!characterRecord.TryGetProperty("mCharacterPassiveSpell", out var passiveEl)
            || passiveEl.ValueKind != JsonValueKind.String)
            return;

        var passiveKey = passiveEl.GetString();
        if (string.IsNullOrEmpty(passiveKey)) return;
        if (!root.TryGetProperty(passiveKey, out var passiveObj)) return;
        if (!passiveObj.TryGetProperty("mSpell", out var mSpell)) return;

        result["P"] = new BinSkillData
        {
            DataValues = ParseDataValues(mSpell),
            SpellCalculations = ParseSpellCalculations(mSpell),
            EffectAmounts = ParseEffectAmounts(mSpell),
        };
    }

    /// <summary>
    /// Parses a single spell-like element into <see cref="BinSkillData"/>, reusing the exact
    /// DataValue/SpellCalculation part-parsing used for champion spells. The element must carry
    /// the champion-BIN sub-shape: a <c>"DataValues"</c> array of <c>{name, values[]}</c> and a
    /// <c>"mSpellCalculations"</c> object of calc-name → formula tree. Item procs (Nashor on-hit,
    /// spellblade, …) share that <c>mFormulaParts</c> shape, so the item-effect loader
    /// (<see cref="Overlay.Core.Combo.ItemEffectDb"/>) evaluates them through the same
    /// <see cref="FormulaInterpreter"/> instead of a parallel parser (see T3.2). Pure parsing.
    /// </summary>
    internal static BinSkillData ParseSpell(JsonElement spell) => new()
    {
        DataValues = ParseDataValues(spell),
        SpellCalculations = ParseSpellCalculations(spell),
        EffectAmounts = ParseEffectAmounts(spell),
    };

    private static JsonElement? FindByType(JsonElement root, string type)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("__type", out var t)
                && t.GetString() == type)
            {
                return prop.Value;
            }
        }
        return null;
    }

    private static Dictionary<string, double[]> ParseDataValues(JsonElement mSpell)
    {
        var result = new Dictionary<string, double[]>();
        if (!mSpell.TryGetProperty("DataValues", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in arr.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out var nameEl)) continue;
            var name = nameEl.GetString();
            if (name is null) continue;
            var values = entry.TryGetProperty("values", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Array
                ? valuesEl.EnumerateArray().Select(v => v.GetDouble()).ToArray()
                : Array.Empty<double>();
            result[name] = values;
        }
        return result;
    }

    /// <summary>Parses the spell's <c>mEffectAmount</c> table — an array of
    /// <c>SpellEffectAmount</c> objects each carrying a per-rank <c>value</c> array — into a
    /// 0-based list of per-rank arrays. An <see cref="CalculationPart.EffectIndex"/> (1-based)
    /// selects one entry. Entries with no <c>value</c> (present but empty in the BIN) become
    /// an empty array, preserving positional 1-based indexing. Kept raw per the BIN Data Model
    /// note — no pre-multiply.</summary>
    private static List<double[]> ParseEffectAmounts(JsonElement mSpell)
    {
        var result = new List<double[]>();
        if (!mSpell.TryGetProperty("mEffectAmount", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in arr.EnumerateArray())
        {
            var values = entry.TryGetProperty("value", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Array
                ? valuesEl.EnumerateArray().Select(v => v.GetDouble()).ToArray()
                : Array.Empty<double>();
            result.Add(values);
        }
        return result;
    }

    private static Dictionary<string, GameCalculation> ParseSpellCalculations(JsonElement mSpell)
    {
        var result = new Dictionary<string, GameCalculation>();
        if (!mSpell.TryGetProperty("mSpellCalculations", out var calcsEl) || calcsEl.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var calc in calcsEl.EnumerateObject())
        {
            result[calc.Name] = ParseGameCalculation(calc.Value);
        }
        return result;
    }

    private static GameCalculation ParseGameCalculation(JsonElement calc)
    {
        if (calc.TryGetProperty("mModifiedGameCalculation", out var modifiedEl))
        {
            // A GameCalculationModified scales the referenced calc by mMultiplier (a single
            // CalculationPart). Absent multiplier ⇒ pass-through (e.g. Aatrox QEdgeDamage).
            CalculationPart? multiplier = calc.TryGetProperty("mMultiplier", out var mulEl)
                ? ParsePart(mulEl)
                : null;
            return new GameCalculation
            {
                ModifiedGameCalculation = modifiedEl.GetString(),
                Multiplier = multiplier,
            };
        }

        var parts = new List<CalculationPart>();
        if (calc.TryGetProperty("mFormulaParts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in partsEl.EnumerateArray())
            {
                parts.Add(ParsePart(part));
            }
        }
        // A plain (non-Modified) GameCalculation can ALSO carry mMultiplier (e.g. Skarner
        // passive PercentHealthDamage: mFormulaParts is a ByCharLevelInterpolation 5..9,
        // scaled down to a 0..1 fraction by mMultiplier=0.01) — capture it the same way the
        // GameCalculationModified branch above does, so FormulaInterpreter can apply it.
        CalculationPart? plainMultiplier = calc.TryGetProperty("mMultiplier", out var plainMulEl)
            ? ParsePart(plainMulEl)
            : null;
        return new GameCalculation { FormulaParts = parts, Multiplier = plainMultiplier };
    }

    private static CalculationPart ParsePart(JsonElement part)
    {
        var type = part.TryGetProperty("__type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "NumberCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.Number,
                    Number = GetDouble(part, "mNumber"),
                };
            case "NamedDataValueCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.NamedDataValue,
                    DataValue = GetString(part, "mDataValue"),
                };
            case "StatByNamedDataValueCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.StatByNamedDataValue,
                    DataValue = GetString(part, "mDataValue"),
                    Stat = GetInt(part, "mStat"),
                    StatFormula = GetInt(part, "mStatFormula"),
                };
            case "StatByCoefficientCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.StatByCoefficient,
                    Coefficient = GetDouble(part, "mCoefficient"),
                    Stat = GetInt(part, "mStat"),
                    StatFormula = GetInt(part, "mStatFormula"),
                };
            case "SumOfSubPartsCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.SumOfSubParts,
                    SubParts = ParseSubParts(part),
                };
            case "ProductOfSubPartsCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.ProductOfSubParts,
                    SubParts = ParseSubParts(part),
                };
            case "ByCharLevelBreakpointsCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.ByCharLevelBreakpoints,
                    Level1Value = GetDouble(part, "mLevel1Value"),
                    InitialBonusPerLevel = GetDouble(part, "mInitialBonusPerLevel"),
                    Breakpoints = ParseBreakpoints(part),
                };
            case "ByCharLevelInterpolationCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.ByCharLevelInterpolation,
                    InterpolationStart = GetDouble(part, "mStartValue"),
                    InterpolationEnd = GetDouble(part, "mEndValue"),
                };
            // (§16 P-round) K'Sante mark %HP: an unlabeled-hash part that linearly interpolates two
            // NAMED DataValues by champion level (min → max) — the DataValue-sourced sibling of
            // ByCharLevelInterpolationCalculationPart. CommunityDragon leaves the __type and both field
            // names as content hashes, so they are read by the exact hashes observed in ksante.bin.json
            // ({ee18a47b} appears only there). A missing/renamed hash → null name → LookupDataValue
            // throws → the calc resolves to null and the curated %HP hit is dropped (safe fallback).
            case "{ee18a47b}":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.ByCharLevelInterpolationByDataValue,
                    InterpolationStartDataValue = GetString(part, "{0589a59c}"), // -> "MarkDamagePercentMin"
                    InterpolationEndDataValue = GetString(part, "{0b65bc23}"),   // -> "MarkDamagePercentMax"
                };
            case "EffectValueCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.EffectValue,
                    EffectIndex = GetInt(part, "mEffectIndex"),
                };
            case "AbilityResourceByCoefficientCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.AbilityResourceByCoefficient,
                    Coefficient = GetDouble(part, "mCoefficient"),
                    StatFormula = GetInt(part, "mStatFormula"),
                };
            case "ByCharLevelFormulaCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.ByCharLevelFormula,
                    PerLevelValues = ParseByCharLevelFormulaValues(part),
                };
            case "StatBySubPartCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.StatBySubPart,
                    Stat = GetInt(part, "mStat"),
                    StatFormula = GetInt(part, "mStatFormula"),
                    SubPart = part.TryGetProperty("mSubpart", out var subPartEl) ? ParsePart(subPartEl) : null,
                };
            case "BuffCounterByCoefficientCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.BuffCounterByCoefficient,
                    Coefficient = GetDouble(part, "mCoefficient"),
                };
            case "BuffCounterByNamedDataValueCalculationPart":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.BuffCounterByNamedDataValue,
                    DataValue = GetString(part, "mDataValue"),
                };
            // (loop 488) Riot's hashed type for "the value of another calculation on this spell".
            // Matched on the hash because that is all the BIN dump gives us; the field name
            // mSpellCalculationKey is the confirming signal and is unique to this shape.
            case "{f3cbe7b2}":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.SpellCalculationRef,
                    SpellCalculationKey = GetString(part, "mSpellCalculationKey"),
                };
            // (loop 488) Hashed type AND hashed fields: {91d404a5} is the base DataValue name and
            // {b2cd0eb0} the per-level one. Rare — one champion in the corpus — but that champion's
            // passive on-hit was curated and resolving to nothing because of it.
            case "{b22609db}":
                return new CalculationPart
                {
                    PartKind = CalculationPart.Kind.NamedDataValueByCharLevel,
                    DataValue = GetString(part, "{91d404a5}"),
                    DataValuePerLevel = GetString(part, "{b2cd0eb0}"),
                };
            default:
                return new CalculationPart { PartKind = CalculationPart.Kind.Unknown };
        }
    }

    private static IReadOnlyList<CalculationPart> ParseSubParts(JsonElement part)
    {
        if (part.TryGetProperty("mSubparts", out var subPartsEl) && subPartsEl.ValueKind == JsonValueKind.Array)
            return subPartsEl.EnumerateArray().Select(ParsePart).ToList();

        // Some champions' BIN represents a two-operand Sum/ProductOfSubPartsCalculationPart
        // via fixed "mPart1"/"mPart2" fields instead of the "mSubparts" array (e.g. Taric's
        // passive TotalDamage's ProductOfSubPartsCalculationPart, and the nested product inside
        // Taric's own CDR calc — confirmed by comparing both shapes directly in taric.bin.json:
        // SumOfSubPartsCalculationPart there always uses "mSubparts", while every
        // ProductOfSubPartsCalculationPart observed uses "mPart1"/"mPart2" instead). Structurally
        // these are just a fixed 2-element subparts list, so read them the same way when
        // "mSubparts" is absent — falling through to an empty list previously made the product
        // silently evaluate to a bogus no-op 1.0 multiplier instead of the real value.
        if (part.TryGetProperty("mPart1", out var part1El) && part.TryGetProperty("mPart2", out var part2El))
            return new List<CalculationPart> { ParsePart(part1El), ParsePart(part2El) };

        return Array.Empty<CalculationPart>();
    }

    /// <summary>ByCharLevelFormulaCalculationPart.values — a flat per-level array (verified in
    /// Leona/Khazix/Lux passive TotalDamage calcs: field name is "values", not "mValues" or
    /// similar). Kept raw here; <see cref="FormulaInterpreter"/> indexes it by champion level.</summary>
    private static IReadOnlyList<double> ParseByCharLevelFormulaValues(JsonElement part)
    {
        if (!part.TryGetProperty("values", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<double>();
        return arr.EnumerateArray().Select(v => v.GetDouble()).ToList();
    }

    private static IReadOnlyList<(int Level, double? AdditionalBonus, double? PerLevelRate)> ParseBreakpoints(
        JsonElement part)
    {
        if (!part.TryGetProperty("mBreakpoints", out var bpEl) || bpEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<(int, double?, double?)>();

        var result = new List<(int, double?, double?)>();
        foreach (var bp in bpEl.EnumerateArray())
        {
            var level = GetInt(bp, "mLevel") ?? 0;
            // Two distinct breakpoint flavors observed in live BIN data (kept separate, not
            // merged — see CalculationPart.Breakpoints remarks): a one-off bump
            // ("mAdditionalBonusAtThisLevel", e.g. Ekko/Akali) vs a per-level growth-rate
            // change from this level onward ("mBonusPerLevelAtAndAfter", e.g.
            // Diana/Lissandra/Ziggs).
            var additionalBonus = GetDouble(bp, "mAdditionalBonusAtThisLevel");
            var perLevelRate = GetDouble(bp, "mBonusPerLevelAtAndAfter");
            result.Add((level, additionalBonus, perLevelRate));
        }
        return result;
    }

    private static double? GetDouble(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? GetInt(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static string? GetString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Per-skill BIN data extracted by <see cref="ChampionBinParser"/>, merged into
/// the matching <see cref="SkillData"/> slot by <see cref="ChampionRepository"/>.</summary>
public sealed class BinSkillData
{
    public Dictionary<string, double[]> DataValues { get; init; } = new();
    public Dictionary<string, GameCalculation> SpellCalculations { get; init; } = new();

    /// <summary>Spell effect-amount table (BIN <c>mSpell.mEffectAmount</c>), 0-based; a
    /// <see cref="CalculationPart.EffectIndex"/> selects an entry 1-based. See
    /// <see cref="SkillData.EffectAmounts"/>.</summary>
    public IReadOnlyList<double[]> EffectAmounts { get; init; } = Array.Empty<double[]>();
}
