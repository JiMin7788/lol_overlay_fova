using Overlay.Core.ChampionDb;

namespace Overlay.Core.Spells;

/// <summary>
/// M12 spec's own input shape for a summoner spell's un-modified cooldown
/// (spec Data Model: <c>SpellBaseData { spellId, baseCooldown }</c>).
///
/// Caller-supplied, not M11-sourced: M12's own Dependencies section lists only
/// <see cref="RuneRepository"/>/<see cref="ItemRepository"/>, and no summoner-spell
/// repository exists anywhere in this codebase (M11's Agent Report / actual
/// `ChampionDb/*.cs` files expose champion/rune/item data only — no
/// `SummonerSpellRepository`). Inventing one here would mean synthesizing a data
/// source this module was never given, which AGENT_GUIDE forbids ("타 모듈의 내부
/// 구현에 직접 의존하지 않는다" / don't guess at another module's unpublished contract) —
/// same boundary M05/M03 already established for their own out-of-scope inputs. See
/// Agent Report "Notes for Reviewer" for the full reasoning.
/// </summary>
public sealed record SpellBaseData(string SpellId, double BaseCooldown);

/// <summary>M12 spec's <c>getCaseTable</c> return shape — all 4 cooldown variants for
/// one <see cref="SpellBaseData"/>.</summary>
public sealed record CooldownCaseTable(double None, double RuneOnly, double BootsOnly, double Both);

/// <summary>
/// M12 Spell Timer: a pure, on-demand cooldown calculator. Given a spell's base
/// cooldown and two caller-supplied booleans (does the player have Cosmic Insight /
/// Ionian Boots of Lucidity), returns the reduced cooldown using the exact formula
/// from the spec's Internal Logic section. Nothing in this file stores state, ticks,
/// or is called on a schedule — every call is a single stateless computation (spec
/// "감지 방식" #3: "표시는 1회성 조회/갱신이며, 자동으로 흐르는(ticking) 카운트다운 형태로
/// 구현하지 않는다").
///
/// <b>Detection-ownership decision (spec "감지 방식" vs. Interfaces):</b> the spec's
/// Interfaces section gives <c>hasCosmicInsight</c>/<c>hasIonianBoots</c> to
/// <c>getFinalCooldown</c> as plain boolean PARAMETERS, not something SpellTimer
/// derives itself. This module implements exactly that: it never reads
/// <c>GameSnapshot</c>/Live Client data at all. Checked against the real M01 output
/// (<c>src/Overlay.Core/GameSnapshot.cs</c>, populated by <c>LiveDataParser.ReadPlayer</c>
/// et al.): <see cref="Overlay.Core.ScoreboardEntry"/> exposes <c>ItemIds</c> (so an
/// Ionian-Boots-owned check against parsed item ids is genuinely possible for a
/// caller to build) but the snapshot has NO rune field anywhere — `GameSnapshot.cs`
/// has zero rune-related members, and Riot's real Live Client Data API in fact does
/// not publish a player's rune page at all. So "detect Cosmic Insight from Live
/// Client data" cannot be honestly implemented by anyone today, in this module or
/// any other. Keeping `SpellTimer` as pure calculation (matching M05/M06's own
/// "caller supplies the boolean, this module just computes" pattern) is the only
/// honest option — see Agent Report Notes for Reviewer for the full writeup and the
/// gap this leaves for whoever eventually builds the M02/M04 caller.
///
/// <b>Ability Haste sourcing (never hardcoded, per Policy Compliance Checklist):</b>
/// - Cosmic Insight: <see cref="RuneRepository.Get"/> keyed by <see cref="CosmicInsightRuneId"/>.
///   The real `RuneData` type has no numeric `effectValue` field (unlike the spec's
///   literal `.effectValue` reference) — only `EffectFormula: string?`. A flat rune
///   effect like "+18 Ability Haste" is representable as a trivial formula with no
///   variables (`EffectFormula = "18"`), resolved via the same
///   <see cref="FormulaParser.Evaluate"/> M03/M06 already use for spell coefficients.
///   No new field was added to M11's `RuneData` (out of this module's Dependencies).
/// - Ionian Boots of Lucidity: <see cref="ItemRepository.Get"/> keyed by
///   <see cref="IonianBootsItemId"/>, reading <c>ItemData.Stats.Haste</c> directly —
///   this field is already a real numeric double on M11's actual `ItemStats` type, so
///   no formula-string workaround is needed here.
/// </summary>
public static class SpellTimer
{
    /// <summary>Rune id key this module looks up via <see cref="RuneRepository"/>.
    /// The M12 spec itself writes the lookup as <c>RuneRepository.get('CosmicInsight')</c>
    /// — this constant is that literal string. Whatever numeric Data Dragon rune id
    /// M11 actually assigns Cosmic Insight must be registered under this same key for
    /// the lookup to resolve; that alignment is M11/Lead's responsibility (M12's
    /// Dependencies section only lists reading M11's repositories, not deciding their
    /// key scheme).</summary>
    public const string CosmicInsightRuneId = "CosmicInsight";

    /// <summary>Item id key this module looks up via <see cref="ItemRepository"/>, same
    /// literal-spec-string reasoning as <see cref="CosmicInsightRuneId"/>.</summary>
    public const string IonianBootsItemId = "IonianBoots";

    private static readonly IReadOnlyDictionary<string, double> EmptyFormulaVars = new Dictionary<string, double>();

    /// <summary>Spec Interfaces: <c>getFinalCooldown(spellId, hasCosmicInsight, hasIonianBoots) -> number</c>.
    /// <paramref name="spell"/> stands in for the spec's <c>spellId</c> parameter — the
    /// caller passes the already-resolved <see cref="SpellBaseData"/> (see that type's
    /// doc for why SpellTimer doesn't resolve `spellId -> baseCooldown` itself).</summary>
    public static double GetFinalCooldown(SpellBaseData spell, bool hasCosmicInsight, bool hasIonianBoots)
    {
        double totalAbilityHaste = 0;
        if (hasCosmicInsight) totalAbilityHaste += GetCosmicInsightHaste();
        if (hasIonianBoots) totalAbilityHaste += GetIonianBootsHaste();

        return spell.BaseCooldown / (1 + totalAbilityHaste / 100.0);
    }

    /// <summary>Spec Interfaces: <c>getCaseTable(spellId) -> { none, runeOnly, bootsOnly, both }</c>
    /// — the same formula applied to all 4 boolean permutations.</summary>
    public static CooldownCaseTable GetCaseTable(SpellBaseData spell)
    {
        return new CooldownCaseTable(
            None: GetFinalCooldown(spell, hasCosmicInsight: false, hasIonianBoots: false),
            RuneOnly: GetFinalCooldown(spell, hasCosmicInsight: true, hasIonianBoots: false),
            BootsOnly: GetFinalCooldown(spell, hasCosmicInsight: false, hasIonianBoots: true),
            Both: GetFinalCooldown(spell, hasCosmicInsight: true, hasIonianBoots: true));
    }

    private static double GetCosmicInsightHaste()
    {
        var rune = RuneRepository.Get(CosmicInsightRuneId)
            ?? throw new InvalidOperationException(
                $"M11 RuneRepository has no entry for '{CosmicInsightRuneId}'. SpellTimer never " +
                "hardcodes an Ability Haste fallback (Policy Compliance Checklist) — M11 must be " +
                "initialized with a Cosmic Insight rune registered under this id before calling " +
                "GetFinalCooldown(hasCosmicInsight: true, ...).");

        if (rune.EffectFormula is null)
            throw new InvalidOperationException(
                $"M11 rune '{CosmicInsightRuneId}' has a null EffectFormula; SpellTimer cannot " +
                "resolve its Ability Haste value without one.");

        return FormulaParser.Evaluate(rune.EffectFormula, EmptyFormulaVars);
    }

    private static double GetIonianBootsHaste()
    {
        var item = ItemRepository.Get(IonianBootsItemId)
            ?? throw new InvalidOperationException(
                $"M11 ItemRepository has no entry for '{IonianBootsItemId}'. SpellTimer never " +
                "hardcodes an Ability Haste fallback (Policy Compliance Checklist) — M11 must be " +
                "initialized with an Ionian Boots of Lucidity item registered under this id before " +
                "calling GetFinalCooldown(hasIonianBoots: true, ...).");

        return item.Stats.Haste;
    }
}
