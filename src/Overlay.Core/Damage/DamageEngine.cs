namespace Overlay.Core.Damage;

/// <summary>
/// M05 Damage Engine: pure calculation engine over a combo node sequence (M03 output,
/// not implemented yet) plus attacker/defender stats. Implements the spec's 6-step
/// Damage Pipeline exactly (see docs/modules/M05_DAMAGE_ENGINE.md Internal Logic).
///
/// This engine does not fetch, estimate, or default any stat itself (Policy Compliance
/// Checklist) — every input field is caller-supplied truth. It does not look up skill
/// coefficients from a champion id; <see cref="ComboNode"/> already carries resolved
/// <c>RatioAD</c>/<c>RatioAP</c>/<c>Damage</c> values (see Notes for Reviewer in the
/// M05 agent report for why: M03/M11 don't exist yet, and M05's own Interfaces
/// pseudocode consumes flat ratios, not a champion id + FormulaInterpreter call).
///
/// No I/O, no UI, no live game access — deterministic math only.
/// </summary>
public sealed class DamageEngine
{
    /// <summary>
    /// Standard League critical-strike damage multiplier used when a node is marked
    /// <see cref="ComboNode.CanCrit"/>. Not specified numerically anywhere in the M05
    /// spec (Step 4 only says "critical multiplier" without a value) — 1.75 is the
    /// current base League crit-damage multiplier absent crit-damage-boosting items/
    /// keystones, which this engine has no input field for. Documented assumption,
    /// see M05 agent report Notes for Reviewer.
    /// </summary>
    public const double CritDamageMultiplier = 1.75;

    /// <summary>Number of bisection iterations used to solve <c>killThresholdHP</c>.
    /// 60 iterations over any realistic HP range (0..a few thousand) converges to
    /// well under 0.01 HP precision; cheap since this is not a hot path.</summary>
    private const int KillThresholdBisectionIterations = 60;

    public DamageCalcResult Calculate(DamageCalcInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Nodes);
        ArgumentNullException.ThrowIfNull(input.Attacker);
        ArgumentNullException.ThrowIfNull(input.Defender);

        var sim = Simulate(input.Nodes, input.Attacker, input.Defender);
        // M20 §6/§11: prefer the O(n) closed-form fold when it applies (no Threshold
        // node, zero shield — see Fold's doc comment); SolveKillThresholdHp (bisection)
        // remains the general-case fallback for everything else (§7).
        double killThresholdHp = SolveKillThresholdHpFold(input.Nodes, input.Attacker, input.Defender)
            ?? SolveKillThresholdHp(input.Nodes, input.Attacker, input.Defender);

        var breakdown = new List<NodeDamage>(sim.Breakdown.Count);
        foreach (var (nodeId, damage) in sim.Breakdown)
            breakdown.Add(new NodeDamage(nodeId, Math.Round(damage, 2)));

        return new DamageCalcResult(
            TotalDamage: Math.Round(sim.TotalDamage, 2),
            Breakdown: breakdown,
            RemainingHP: Math.Round(sim.RemainingHP, 2),
            IsLethal: sim.IsLethal,
            KillThresholdHP: Math.Round(killThresholdHp, 2),
            LifestealHeal: Math.Round(sim.LifestealHeal, 2),
            TotalDamageMin: Math.Round(sim.TotalDamageMin, 2),
            TotalDamageMax: Math.Round(sim.TotalDamageMax, 2),
            RemainingHPMin: Math.Round(sim.RemainingHPMin, 2),
            RemainingHPMax: Math.Round(sim.RemainingHPMax, 2));
    }

    /// <summary>
    /// Runs the full node sequence once against a concrete defender current-HP value,
    /// at full double precision (no rounding — Agent Implementation Notes). Shared by
    /// <see cref="Calculate"/> and the killThresholdHP bisection probe so both use
    /// exactly the same pipeline logic.
    ///
    /// Computes THREE parallel tracks per node, each with its own running remainingHp/
    /// shieldRemaining/executed state (a crit landing on an early node changes how much
    /// HP is left for a later MissingHp/CurrentHp node, so the tracks cannot share a
    /// single remainingHp variable):
    /// <list type="bullet">
    /// <item><b>Blend</b> — the pre-existing expected-value track (unchanged formula/
    /// behavior): a <see cref="ComboNode.CanCrit"/> node's raw damage is scaled by
    /// <c>1 + critChance*(CritDamageMultiplier-1)</c>, an average across crit/no-crit
    /// outcomes. Feeds <see cref="DamageCalcResult.TotalDamage"/>/<c>RemainingHP</c> for
    /// any consumer that still wants a single blended number.</item>
    /// <item><b>Min</b> — the worst-case-for-damage / best-case-for-survival track: every
    /// <see cref="ComboNode.CanCrit"/> node is assumed to NOT crit. Feeds
    /// <see cref="DamageCalcResult.TotalDamageMin"/>/<c>RemainingHPMin</c>.</item>
    /// <item><b>Max</b> — the best-case-for-damage track: every <see cref="ComboNode.CanCrit"/>
    /// node with a nonzero <see cref="AttackerStat.CriticalChance"/> is assumed to crit with
    /// certainty (guaranteed-crit, not scaled by the actual chance — this is the "best case",
    /// not a probability-weighted value). A node with 0% crit chance never crits even on this
    /// track (nothing to assume). Feeds <see cref="DamageCalcResult.TotalDamageMax"/>/
    /// <c>RemainingHPMax</c>, and this track's terminal executed/dead state is what
    /// <see cref="DamageCalcResult.IsLethal"/> now reports (see its own doc comment) — i.e.
    /// "kill possible" means the BEST case (all crits landing) reaches 0 HP, not that a kill
    /// is guaranteed.</item>
    /// </list>
    /// When no node in the sequence is both <see cref="ComboNode.CanCrit"/> and hit by a
    /// nonzero <see cref="AttackerStat.CriticalChance"/>, Min and Max are identical by
    /// construction (no variance to diverge them).
    /// </summary>
    private static SimResult Simulate(IReadOnlyList<ComboNode> nodes, AttackerStat attacker, DefenderStat defender)
    {
        var blend = new TrackState(defender.CurrentHP, defender.Shield);
        var min = new TrackState(defender.CurrentHP, defender.Shield);
        var max = new TrackState(defender.CurrentHP, defender.Shield);

        var breakdown = new List<(string NodeId, double Damage)>(nodes.Count);

        foreach (var node in nodes)
        {
            // Step 1: base damage (identical across all three tracks — it does not depend
            // on remainingHp, only the execute-type adjustments below do).
            double baseRaw = node.Damage
                + node.RatioAD * (attacker.Ad + attacker.BonusAD)
                + node.RatioAP * attacker.Ap;

            double blendDamage = ProcessNode(node, baseRaw, attacker, defender, blend, CritTrack.Blend);
            breakdown.Add((node.NodeId, blendDamage));

            ProcessNode(node, baseRaw, attacker, defender, min, CritTrack.Min);
            ProcessNode(node, baseRaw, attacker, defender, max, CritTrack.Max);
        }

        if (blend.RemainingHp < 0) blend.RemainingHp = 0;
        if (min.RemainingHp < 0) min.RemainingHp = 0;
        if (max.RemainingHp < 0) max.RemainingHp = 0;

        return new SimResult(
            blend.TotalDamage, breakdown, blend.RemainingHp, max.Executed, blend.Lifesteal,
            min.TotalDamage, min.RemainingHp, max.TotalDamage, max.RemainingHp);
    }

    /// <summary>Which crit assumption a <see cref="TrackState"/> pass uses for a
    /// <see cref="ComboNode.CanCrit"/> node — see <see cref="Simulate"/>'s doc comment.
    /// Internal (not private) so M20's linear-fold methods (<see cref="Fold"/> and
    /// friends) and their tests can select a track without duplicating this type.</summary>
    internal enum CritTrack { Blend, Min, Max }

    /// <summary>
    /// Runs the full per-node pipeline (execute-type raw adjustment -> crit -> mitigation
    /// -> lifesteal -> shield/HP -> execute check) for ONE track's already-accumulated
    /// state, mutating <paramref name="track"/> in place and returning this node's
    /// post-mitigation damage (for the blend track's breakdown list). If the track's
    /// target is already dead (from an earlier node in this same track), the node
    /// contributes 0 and is otherwise skipped — same completeness contract as before,
    /// just evaluated independently per track.
    /// </summary>
    private static double ProcessNode(
        ComboNode node, double baseRaw, AttackerStat attacker, DefenderStat defender,
        TrackState track, CritTrack critTrack)
    {
        if (track.Executed) return 0;

        // (§27 (B), dynamic low-HP on-hit gate) A hit tagged with HpBelowGateFraction contributes
        // NOTHING until THIS track's running HP has depleted to/below that fraction of max HP — a
        // low-HP-only proc (Ekko W passive) that turns on partway through the combo as the per-track
        // hypothetical HP falls below the line. Per-track: the min track (less prior damage → higher
        // running HP) can still be above the line while the max track is below it, so the range
        // diverges correctly. Ungated nodes (fraction null) skip this entirely — no behavior change.
        if (node.HpBelowGateFraction is { } gate && track.RemainingHp > gate * defender.MaxHP)
            return 0;

        double raw = baseRaw;

        // Execute types that alter/replace the base damage itself happen before
        // mitigation, since they are still raw (pre-armor/mr) damage values. Reads THIS
        // track's own remainingHp, so min/max diverge correctly once an earlier crit (or
        // lack thereof) has changed how much HP is left on this track.
        switch (node.ExecuteType)
        {
            case ExecuteType.CurrentHp:
                // "재계산" (recalculate): this node's damage is a percentage of
                // the defender's HP remaining *at the moment it lands*, replacing
                // the flat ratioAD/ratioAP formula (tick-type execute skills).
                raw = node.ExecutePercent * track.RemainingHp;
                break;
            case ExecuteType.MissingHp:
                // Additive bonus based on HP already missing at this point in the
                // sequence (dynamic, not the pre-combo defender.currentHP snapshot
                // — consistent with CurrentHp's own dynamic re-evaluation above).
                raw += node.ExecutePercent * (defender.MaxHP - track.RemainingHp);
                break;
            case ExecuteType.BaseWithMissingHpBonus:
                // Kraken Slayer "Bring It Down" shape (wiki-confirmed, see
                // item_effects.json's 3095 _note): raw is the flat base damage already
                // folded above (no AD/AP ratio for this proc), scaled up by up to
                // ExecutePercent (0.75 for Kraken Slayer) times the TARGET's missing-
                // health FRACTION at the moment this node lands — a multiplicative
                // base×(1 + scalar×missingHpFraction) shape, distinct from MissingHp's
                // additive fraction×absoluteMissingHP term above. Re-evaluated live off
                // the same dynamic remainingHp used by CurrentHp/MissingHp, not a frozen
                // pre-combo snapshot.
                double missingFrac = defender.MaxHP > 0 ? (defender.MaxHP - track.RemainingHp) / defender.MaxHP : 0;
                raw *= 1.0 + node.ExecutePercent * missingFrac;
                break;
        }

        // Step 4 (critical multiplier only; lifesteal is handled separately below
        // and never feeds back into damage - see Notes for Reviewer for why crit
        // is applied here, before Step 2 mitigation, rather than after Step 3 as
        // the spec's step *numbering* literally suggests). Extracted to
        // <see cref="CritFactor"/> (M20 fold work) so the O(n) closed-form fold can
        // reuse the exact same per-track crit rule instead of a second, divergence-
        // prone copy — see that method's doc comment.
        raw *= CritFactor(node, attacker, critTrack);

        // Step 2: mitigation by damage type. Physical/Magic apply the target's
        // shred (reduction) then the attacker's penetration in League order via
        // EffectiveResistMultiplier; True is unmitigated. Extracted to
        // <see cref="MitigationMultiplier"/> (M20 fold work) for the same reuse-not-
        // duplicate reason as CritFactor above.
        double mitigated = raw * MitigationMultiplier(node.DamageType, attacker, defender);

        track.TotalDamage += mitigated;

        // Lifesteal: separate output field, computed off this node's post-
        // mitigation damage; never added back into totalDamage/remainingHP. Only
        // meaningful/consumed on the Blend track (DamageCalcResult.LifestealHeal).
        track.Lifesteal += mitigated * attacker.LifeSteal;

        // Step 3: shield absorbs first, only the excess hits HP. Shield pool is
        // cumulative across the whole sequence (a partially-depleted shield still
        // absorbs part of the next node).
        double hpBeforeShield = track.RemainingHp; // (T1-b trace) captured before shield deduction
        double hpLoss;
        double shieldAbsorbed = 0; // (T1-b trace) stays 0 in the no-shield branch
        if (track.ShieldRemaining > 0)
        {
            shieldAbsorbed = Math.Min(track.ShieldRemaining, mitigated);
            track.ShieldRemaining -= shieldAbsorbed;
            hpLoss = mitigated - shieldAbsorbed;
        }
        else
        {
            hpLoss = mitigated;
        }
        track.RemainingHp -= hpLoss;

        // Step 5: execute checks.
        if (node.ExecuteType == ExecuteType.Threshold && track.RemainingHp <= node.ExecuteThreshold)
        {
            track.RemainingHp = 0;
            track.Executed = true;
        }
        else if (track.RemainingHp <= 0)
        {
            track.RemainingHp = 0;
            track.Executed = true;
        }

        // (T1-b, M26 §6 Trace Mode) Blend-track-only observation row — Min/Max are skipped to avoid
        // 3x noise, since Blend is the breakdown/display track (see CalcTrace's doc comment).
        if (critTrack == CritTrack.Blend && CalcTrace.IsCollecting)
        {
            double k = raw > 0 ? mitigated / raw : 0;
            CalcTrace.Row($"node={node.NodeId} type={node.DamageType} exec={node.ExecuteType} " +
                $"raw={raw:0.####} k={k:0.####} mitigated={mitigated:0.####} " +
                $"armor={defender.Armor:0.##} mr={defender.Mr:0.##} " +
                $"armorRedFlat={defender.ArmorReductionFlat:0.##} armorRedPct={defender.ArmorReductionPercent:0.####} " +
                $"mrRedFlat={defender.MrReductionFlat:0.##} mrRedPct={defender.MrReductionPercent:0.####} " +
                $"armorPenFlat={attacker.ArmorPenFlat:0.##} armorPenPct={attacker.ArmorPenPercent:0.####} " +
                $"magicPenFlat={attacker.MagicPenFlat:0.##} magicPenPct={attacker.MagicPenPercent:0.####} " +
                $"hp={hpBeforeShield:0.##}->{track.RemainingHp:0.##} shieldAbsorbed={shieldAbsorbed:0.##}");
        }

        return mitigated;
    }

    /// <summary>
    /// Resolves the crit-damage multiplier to use for the Max (guaranteed-crit) track:
    /// the attacker's real, live-parsed <see cref="AttackerStat.CritDamageMultiplier"/>
    /// (from the Live Client API's <c>championStats.critDamage</c>) when it is a genuine
    /// positive value, else the documented <see cref="CritDamageMultiplier"/> constant —
    /// conservative fallback per CLAUDE.md P1/P2 (never fabricate a stat the API didn't
    /// actually report; 0/absent is treated as "not reported", not "0x crit damage").
    /// </summary>
    private static double ResolveCritMultiplier(AttackerStat attacker)
        => attacker.CritDamageMultiplier > 0 ? attacker.CritDamageMultiplier : CritDamageMultiplier;

    /// <summary>
    /// The per-track crit multiplier <see cref="ProcessNode"/> applies to a node's raw
    /// damage (Step 4), extracted so <see cref="Fold"/> (M20 §5.1) can fold a node's
    /// crit-scaled damage into its effective <c>B</c>/<c>r</c>/<c>s</c>/<c>p</c> terms
    /// using the exact same rule instead of re-deriving it. Blend uses the probability-
    /// weighted expected-value factor (unchanged constant <see cref="CritDamageMultiplier"/>,
    /// not the attacker's real crit-damage stat — matches the pre-existing Blend
    /// behavior); Min never assumes a crit; Max uses <see cref="ResolveCritMultiplier"/>
    /// (guaranteed crit, real stat if reported). Returns 1.0 (no-op) whenever the node
    /// can't crit or the attacker's crit chance is 0, for any track.
    ///
    /// <para>(M27 — <see cref="ComboNode.CritDamageScalar"/>, default 1.0) A curated ability hit can
    /// crit for only a FRACTION of a full crit's bonus (e.g. Garen E = 0.40, "40% of a standard
    /// crit's bonus") — the scalar <c>s</c> multiplies the <c>(multiplier-1)</c> bonus term on both
    /// Blend and Max: <c>1 + s·(M-1)</c>. Min is unaffected (still never assumes a crit — P2-safe:
    /// this feature only widens the Blend/Max exposure, never raises the displayed floor).
    /// Backward-compat proof: at <c>s = 1.0</c> (every pre-existing crit-capable node, all AAs) this
    /// reduces to the exact prior formulas — Blend = <c>1 + c·(M-1)</c>, Max =
    /// <c>1 + (Mreal-1) = Mreal = ResolveCritMultiplier</c> — so no existing AA's number changes.</para>
    /// </summary>
    private static double CritFactor(ComboNode node, AttackerStat attacker, CritTrack critTrack)
    {
        if (!node.CanCrit || attacker.CriticalChance <= 0) return 1.0;
        double s = node.CritDamageScalar;
        return critTrack switch
        {
            CritTrack.Blend => 1.0 + s * attacker.CriticalChance * (CritDamageMultiplier - 1.0),
            CritTrack.Max => 1.0 + s * (ResolveCritMultiplier(attacker) - 1.0),
            _ => 1.0, // Min: worst-case-for-damage, never assume a crit lands.
        };
    }

    /// <summary>Mutable per-track running state threaded through <see cref="ProcessNode"/>
    /// — one instance per <see cref="CritTrack"/>, never shared across tracks (see
    /// <see cref="Simulate"/>'s doc comment for why each needs its own).</summary>
    private sealed class TrackState
    {
        public double RemainingHp;
        public double ShieldRemaining;
        public double TotalDamage;
        public double Lifesteal;
        public bool Executed;

        public TrackState(double currentHp, double shield)
        {
            RemainingHp = currentHp;
            ShieldRemaining = Math.Max(0, shield);
            Executed = currentHp <= 0;
        }
    }

    /// <summary>
    /// League-accurate effective-resistance mitigation multiplier for one resistance
    /// channel (armor or MR). Order: flat reduction -> percent reduction -> percent
    /// penetration (only once the post-reduction resistance is still positive) -> flat
    /// penetration. Percent penetration alone can only drive the post-reduction
    /// resistance down to (not below) zero; flat penetration (lethality / Void Staff's
    /// flat term via the caller) is applied AFTER that and CAN drive the resistance
    /// negative — matching how lethality genuinely over-penetrates a low-resistance
    /// target in live League (loop 38 pending-changes fix; previously flat pen was
    /// wrongly clamped at 0, silently capping mitigation at "no reduction" instead of
    /// amplifying). A negative resistance (from reduction or flat pen) amplifies damage
    /// via the <c>2 - 100/(100-R)</c> branch instead of the usual <c>100/(100+R)</c>.
    /// </summary>
    private static double EffectiveResistMultiplier(
        double baseResist, double reductionFlat, double reductionPercent,
        double penPercent, double penFlat)
    {
        double r = baseResist;
        r -= reductionFlat;                 // flat reduction (can go below 0)
        r *= 1.0 - reductionPercent;        // % reduction (can go below 0)
        if (r > 0)
        {
            r *= 1.0 - penPercent;          // % pen, never below 0
            r -= penFlat;                   // flat pen / lethality — CAN drive R negative
        }
        return r >= 0 ? 100.0 / (100.0 + r) : 2.0 - 100.0 / (100.0 - r);
    }

    /// <summary>
    /// The per-node post-mitigation multiplier <see cref="ProcessNode"/> applies (Step
    /// 2), extracted so <see cref="Fold"/> (M20 §5.1's <c>k</c>) reuses this instead of
    /// re-deriving it — True damage is unmitigated (multiplier 1.0).
    /// </summary>
    private static double MitigationMultiplier(DamageType type, AttackerStat attacker, DefenderStat defender)
        => type switch
        {
            DamageType.Physical => EffectiveResistMultiplier(
                defender.Armor, defender.ArmorReductionFlat, defender.ArmorReductionPercent,
                attacker.ArmorPenPercent, attacker.ArmorPenFlat),
            DamageType.Magic => EffectiveResistMultiplier(
                defender.Mr, defender.MrReductionFlat, defender.MrReductionPercent,
                attacker.MagicPenPercent, attacker.MagicPenFlat),
            _ => 1.0, // True damage: unmitigated.
        };

    /// <summary>
    /// Step 6's killThresholdHP: a genuine inverse calculation of "the maximum
    /// defender.currentHP for which this exact node sequence (same attacker, armor,
    /// mr, shield, execute rules) would still be lethal" — NOT an echo of
    /// remainingHP. Solved by bisection over defender.currentHP re-running the same
    /// <see cref="Simulate"/> pipeline used for the real result, since the execute
    /// types (especially CurrentHp/MissingHp, which read defender.currentHP/MaxHP
    /// mid-sequence) make a closed-form inverse impractical to derive generically.
    ///
    /// Assumes "dies" is monotonic non-increasing in defender.currentHP (more HP is
    /// never easier to kill through) — true for Threshold and MissingHp nodes by
    /// construction, and true for CurrentHp nodes under normal (non-adversarial)
    /// combos. See M05 agent report Known Issues for the documented edge case.
    ///
    /// Internal (not private) so M20's fold-vs-bisection oracle-agreement tests can
    /// call this directly (DamageEngineFoldTests.cs) — otherwise identical to before.
    /// </summary>
    internal static double SolveKillThresholdHp(IReadOnlyList<ComboNode> nodes, AttackerStat attacker, DefenderStat defender)
    {
        bool DiesAt(double hp)
        {
            var probe = defender with { CurrentHP = hp };
            return Simulate(nodes, attacker, probe).IsLethal;
        }

        if (defender.MaxHP <= 0) return 0;
        if (DiesAt(defender.MaxHP)) return defender.MaxHP;
        if (!DiesAt(0)) return 0; // defensive: 0 HP not lethal should never happen.

        double lo = 0, hi = defender.MaxHP;
        for (int i = 0; i < KillThresholdBisectionIterations; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (DiesAt(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    // ------------------------------------------------------------------------------
    // M20 §5–§6: closed-form linear fold. Companion to SolveKillThresholdHp's
    // bisection above — NOT a replacement (SolveKillThresholdHp stays the general-case
    // authority per M20 §7). See docs/modules/M20_COMBO_DAMAGE_FORMULA.md for the full
    // derivation; this implementation mirrors §5.1's update table character-for-
    // character and was hand-verified against all 7 vectors in §10 before being wired
    // into Calculate() below (see DamageEngineFoldTests.cs).
    // ------------------------------------------------------------------------------

    /// <summary>
    /// M20 §5.1: walks <paramref name="nodes"/> once, folding each node's damage into
    /// running constants <c>(a, b)</c> such that, for this exact node sequence,
    /// <c>H_after = a·H0 - b</c> for ANY starting HP <c>H0</c> (the whole combo is
    /// affine in <c>H0</c> — §5). <c>k</c> (mitigation) comes from
    /// <see cref="MitigationMultiplier"/> and crit scaling from <see cref="CritFactor"/>
    /// — the exact same helpers <see cref="ProcessNode"/> uses, so this can never
    /// numerically diverge from <see cref="Simulate"/>'s forward pipeline.
    ///
    /// Returns <see langword="null"/> when the sequence contains a breaker the affine
    /// assumption can't survive (M20 §7): an <see cref="ExecuteType.Threshold"/> node
    /// (discontinuous jump to 0 HP), or a nonzero <see cref="DefenderStat.Shield"/>.
    /// Shield is checked once up front rather than "has it fully depleted by node i"
    /// per node: shield depletion timing itself depends on H0 once any HP-dependent
    /// shape (MissingHp/CurrentHp/BaseWithMissingHpBonus) precedes full depletion,
    /// which would silently reintroduce the very H0-dependence the fold exists to
    /// eliminate — so this implementation only folds when there is no shield to
    /// deplete at all, and falls back to bisection for the whole combo otherwise
    /// (documented limitation, not a silent wrong answer — M20 §7's "Handling" column).
    /// </summary>
    internal static (double A, double B)? Fold(
        IReadOnlyList<ComboNode> nodes, AttackerStat attacker, DefenderStat defender, CritTrack track)
    {
        if (defender.Shield > 0) return null;

        double a = 1.0, b = 0.0;
        double m = defender.MaxHP;

        foreach (var node in nodes)
        {
            if (node.ExecuteType == ExecuteType.Threshold) return null;

            double baseRaw = node.Damage
                + node.RatioAD * (attacker.Ad + attacker.BonusAD)
                + node.RatioAP * attacker.Ap;
            double cf = CritFactor(node, attacker, track);
            double k = MitigationMultiplier(node.DamageType, attacker, defender);
            double bEff = cf * baseRaw; // crit-scaled B (§5.1 derivations: crit scales the whole raw term)

            switch (node.ExecuteType)
            {
                case ExecuteType.MissingHp:
                    {
                        double r = cf * node.ExecutePercent;
                        double factor = 1.0 + k * r;
                        a *= factor;
                        b = factor * b + k * (bEff + r * m);
                        break;
                    }
                case ExecuteType.CurrentHp:
                    {
                        double s = cf * node.ExecutePercent;
                        double factor = 1.0 - k * s;
                        a *= factor;
                        b = factor * b;
                        break;
                    }
                case ExecuteType.BaseWithMissingHpBonus:
                    {
                        double p = node.ExecutePercent;
                        double c = m > 0 ? k * bEff * p / m : 0.0; // m<=0 mirrors ProcessNode's missingFrac=0 guard
                        a *= 1.0 + c;
                        b = (1.0 + c) * b + k * bEff * (1.0 + p);
                        break;
                    }
                default: // None (flat)
                    b += k * bEff;
                    break;
            }
        }

        return (a, b);
    }

    /// <summary>
    /// M20 §6 item 2: <c>H* = b/a</c> over the whole combo — the O(n) closed-form
    /// companion to <see cref="SolveKillThresholdHp"/>. Uses the <see cref="CritTrack.Max"/>
    /// track to match <see cref="Simulate"/>/<see cref="SolveKillThresholdHp"/>'s own
    /// "IsLethal reports the best-case/guaranteed-crit track" convention (see
    /// <see cref="DamageCalcResult.IsLethal"/>'s doc comment) — both solvers must agree
    /// on what "kill" means for the same input.
    ///
    /// Returns <see langword="null"/> when <see cref="Fold"/> reports the sequence isn't
    /// fold-applicable (Threshold node / nonzero shield); callers should fall back to
    /// <see cref="SolveKillThresholdHp"/> in that case.
    /// </summary>
    internal static double? SolveKillThresholdHpFold(
        IReadOnlyList<ComboNode> nodes, AttackerStat attacker, DefenderStat defender)
    {
        if (defender.MaxHP <= 0) return 0;
        if (Fold(nodes, attacker, defender, CritTrack.Max) is not { } folded) return null;
        if (folded.A == 0) return 0; // degenerate guard: avoid 0/0 or x/0
        double h = folded.B / folded.A;
        return h < 0 ? 0 : h;
    }

    /// <summary>
    /// M20 §6 item 3 / §6.3: folds only the suffix of <paramref name="nodes"/> starting
    /// at <paramref name="startIndex"/> (e.g. the R node) and returns the HP the target
    /// must be at when that node is cast for the remaining tail to exactly kill —
    /// <c>b_suf / a_suf</c>. This is just <see cref="Fold"/> re-run from a clean
    /// <c>(a=1, b=0)</c> on the sub-sequence (the fold has no notion of "prefix state"
    /// to carry in — §6.3's worked identity in M20 §8 confirms this independently:
    /// <c>H*_full - preRFlatDamage = suffixThreshold</c>).
    /// </summary>
    internal static double? SolveSuffixThresholdHp(
        IReadOnlyList<ComboNode> nodes, int startIndex, AttackerStat attacker, DefenderStat defender, CritTrack track)
    {
        if (startIndex < 0 || startIndex > nodes.Count)
            throw new ArgumentOutOfRangeException(nameof(startIndex));

        var suffix = new List<ComboNode>(nodes.Count - startIndex);
        for (int i = startIndex; i < nodes.Count; i++) suffix.Add(nodes[i]);

        if (Fold(suffix, attacker, defender, track) is not { } folded) return null;
        if (folded.A == 0) return 0;
        double h = folded.B / folded.A;
        return h < 0 ? 0 : h;
    }

    /// <summary>
    /// M20 §6.1 ordering-rule helper (P4 display/UX guidance only — never changes
    /// combo execution). Design choice: rather than reimplementing the
    /// <c>Π(1+r_eff)</c> closed form of §6.1 as a second formula that could drift from
    /// <see cref="Fold"/>, this just runs the fold twice — once on the original node
    /// order, once with the node at <paramref name="fromIndex"/> moved to
    /// <paramref name="toIndex"/> — and diffs the resulting <c>H*</c>. Always correct by
    /// construction (it literally IS the fold) at O(n) cost instead of a hypothetical
    /// O(1); acceptable since this is a one-off UI suggestion, not a hot path. Returns
    /// <see langword="null"/> if either arrangement isn't fold-applicable.
    /// </summary>
    internal static double? OrderingDeltaKillThresholdHp(
        IReadOnlyList<ComboNode> nodes, int fromIndex, int toIndex,
        AttackerStat attacker, DefenderStat defender, CritTrack track)
    {
        if (fromIndex == toIndex) return 0.0;

        var reordered = new List<ComboNode>(nodes);
        var moved = reordered[fromIndex];
        reordered.RemoveAt(fromIndex);
        reordered.Insert(toIndex, moved);

        if (Fold(nodes, attacker, defender, track) is not { } original) return null;
        if (Fold(reordered, attacker, defender, track) is not { } changed) return null;
        if (original.A == 0 || changed.A == 0) return null;

        return (changed.B / changed.A) - (original.B / original.A);
    }

    // ------------------------------------------------------------------------------
    // M20 §6.2: kill-feasible maximum for a variance-heavy node (placement search).
    // Companion to §6.1's OrderingDeltaKillThresholdHp above — that one answers what
    // ordering does to the kill THRESHOLD, this one answers what it does to the TOTAL
    // DAMAGE the combo card actually displays. Both are display-only (P3/P4): nothing
    // here reorders, queues or inputs anything, it only reports a number.
    // ------------------------------------------------------------------------------

    /// <summary>Outcome of <see cref="SolveKillFeasibleMaxDamage"/> — see each member.</summary>
    internal enum PlacementStatus
    {
        /// <summary>No HP-basis node in the sequence: the authored total IS the only total,
        /// nothing to optimize (M20 §6.2's "flat / MaxHp → order-independent" row).</summary>
        NoVarianceNode,

        /// <summary>Two or more independently movable HP-basis groups. §6.2's closed form is
        /// derived for ONE moved node, so no search is run and the authored number stands —
        /// reported explicitly rather than silently approximated (CLAUDE_CODE_TODO §75-4).</summary>
        MultipleVarianceGroups,

        /// <summary>Exactly one variance group; <see cref="PlacementSearchResult.OptimalGroupIndex"/>
        /// and <see cref="PlacementSearchResult.MaxTotalDamage"/> are the searched optimum.</summary>
        Optimized,

        /// <summary>Same search as <see cref="Optimized"/>, but the combo already kills with the
        /// variance group REMOVED — so the honest headline is "R unnecessary", not a ceiling
        /// (M20 §6.2 "Degenerate case worth reporting rather than hiding"). The searched numbers
        /// are still returned (they are real, not fabricated); it is the caller's job to label
        /// them honestly rather than present them as a required ceiling.</summary>
        VarianceUnnecessary,
    }

    /// <param name="AuthoredGroupIndex">Group index of the variance group in the AUTHORED order,
    /// or -1 when there is none. Directly comparable to <paramref name="OptimalGroupIndex"/>:
    /// re-inserting the removed group at its own authored index reproduces the authored order.</param>
    /// <param name="OptimalGroupIndex">The <c>j*</c> of M20 §6.2 — the placement whose re-run of
    /// the authored pipeline yields the highest total damage.</param>
    /// <param name="AuthoredTotalDamage">Blend-track total for the authored order (identical to
    /// <see cref="DamageCalcResult.TotalDamage"/> for the same inputs, unrounded).</param>
    /// <param name="MaxTotalDamage">Blend-track total at <paramref name="OptimalGroupIndex"/>.</param>
    internal sealed record PlacementSearchResult(
        PlacementStatus Status,
        int AuthoredGroupIndex,
        int OptimalGroupIndex,
        double AuthoredTotalDamage,
        double MaxTotalDamage);

    /// <summary>
    /// M20 §6.2: the kill-feasible MAXIMUM total damage for a combo containing one
    /// variance-heavy (HP-basis) node — Garen R rank 1 spans 125 (full HP) to 375 (1 HP) on a
    /// 1000-HP target purely by where it sits, and <see cref="Simulate"/> only ever reports the
    /// AUTHORED order's number.
    ///
    /// <para><b>Not the unconstrained maximum.</b> Pushing the variance node later always raises
    /// its own damage, but <see cref="ProcessNode"/>'s <c>if (track.Executed) return 0;</c> zeroes
    /// a node that lands on an already-dead target — so the optimum is the LATEST placement at
    /// which the target is still alive, and the alive constraint (<c>P_j &lt; H0 + Shield</c>;
    /// shield is included because <see cref="TrackState"/> depletes it before HP) is what makes the
    /// answer finite. This search never has to state that constraint: it re-runs the real pipeline,
    /// which already enforces it, and takes the max.</para>
    ///
    /// <para><b>Search space (CLAUDE_CODE_TODO §75-1).</b> Only the variance group moves; every
    /// other group keeps the user's authored relative order. This is not a permutation search — a
    /// combo is a cooldown/cast-constrained user intent, and §6.2's closed form is derived under
    /// exactly this "other nodes fixed, variance node moves" premise.</para>
    ///
    /// <para><b>The movable unit is a CAST GROUP, not a node.</b> <paramref name="groupOfNode"/>
    /// maps each node to the authored cast it came from (contiguous, non-decreasing; null = every
    /// node is its own group). This is load-bearing, not tidiness: Garen R is curated as TWO hits
    /// (missing-HP hit first, then flat 125 — <c>skill_damage/Garen.json</c> <c>_noteR2</c>), and
    /// moving only the missing-HP hit would leave its own sibling's flat damage sitting in the
    /// prefix, so the hit would read 125 damage as freshly-missing health — the exact
    /// 125→156.25 inflation that <c>_noteR2</c>'s hit ordering exists to prevent. The two hits are
    /// one simultaneous cast and must move together.</para>
    ///
    /// <para><b>Which crit track decides the placement (CLAUDE_CODE_TODO §75-2).</b> Blend. The
    /// three tracks carry different running HP, so <c>j*</c> can genuinely differ between them;
    /// this picks the placement on the Blend track and reports it for the card as a whole, matching
    /// the existing convention that Blend is the breakdown/display track (see
    /// <see cref="Simulate"/> and <see cref="CalcTrace"/>). Optimizing each track independently
    /// would make the Max box a true ceiling but would leave the card claiming two different
    /// placements at once, which is not explainable to a player.</para>
    ///
    /// <para><b>Gates are constraints, not targets (CLAUDE_CODE_TODO §75-5).</b>
    /// <see cref="ExecuteType.Threshold"/> and <see cref="ComboNode.HpBelowGateFraction"/> nodes
    /// are never selected as the movable group; they simply re-evaluate against whatever running HP
    /// each candidate placement produces, because the search re-runs <see cref="Simulate"/> rather
    /// than modelling anything. (They do break the §6.2 closed form's affinity, which is why the
    /// closed-form oracle in the tests skips combos containing them — same piecewise-affine reason
    /// as §7.)</para>
    ///
    /// <para>O(n²) by construction: one <see cref="Simulate"/> per candidate placement, n &lt; 10 in
    /// every real combo. Deliberately reuses the existing forward pipeline instead of a second
    /// damage model, so crit tracks, shields, gates and execute rules stay correct for free.</para>
    /// </summary>
    internal static PlacementSearchResult SolveKillFeasibleMaxDamage(
        IReadOnlyList<ComboNode> nodes, IReadOnlyList<int>? groupOfNode,
        AttackerStat attacker, DefenderStat defender)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var groups = BuildPlacementGroups(nodes.Count, groupOfNode);
        double authored = Simulate(nodes, attacker, defender).TotalDamage;

        int varianceGroup = -1, varianceGroupCount = 0;
        var varianceShape = ExecuteType.None;
        for (int g = 0; g < groups.Count; g++)
        {
            var (start, count) = groups[g];
            for (int i = start; i < start + count; i++)
            {
                if (nodes[i].ExecuteType is not (ExecuteType.MissingHp or ExecuteType.CurrentHp
                    or ExecuteType.BaseWithMissingHpBonus)) continue;
                varianceGroupCount++;
                varianceGroup = g;
                varianceShape = nodes[i].ExecuteType; // a group mixing shapes takes its first one
                break;
            }
        }

        if (varianceGroupCount == 0)
            return new PlacementSearchResult(PlacementStatus.NoVarianceNode, -1, -1, authored, authored);
        if (varianceGroupCount > 1)
            return new PlacementSearchResult(
                PlacementStatus.MultipleVarianceGroups, varianceGroup, varianceGroup, authored, authored);

        // The search itself. Placement index j is expressed in "groups with the variance group
        // removed" coordinates, so j == AuthoredGroupIndex reproduces the authored order exactly.
        //
        // The maximum is taken over ALIVE placements only — placements at which the target is still
        // standing when the variance group lands (§6.2's `P_j < H0 + Shield`; the prefix simulation
        // below enforces it through the real pipeline, shield depletion and gates included, rather
        // than restating it as a formula). Filtering matters: past the alive boundary the variance
        // group contributes 0, yet the raw total can still be HIGHER there because TotalDamage
        // counts the full damage of whatever node landed the kill, overkill included. Ranking on
        // the unfiltered total would therefore "optimize" by picking a placement where the node
        // being placed does nothing at all.
        //
        // Within the alive set the damage numbers pick the winner and §6.2's shape table only
        // breaks TIES: MissingHp/BaseWithMissingHpBonus grow with the prefix, so on a tie the
        // LATEST alive placement wins (`j*`); CurrentHp falls as the target drops, so the earliest
        // does. Ties are not exotic — a shield decouples running HP from prefix damage entirely
        // (TrackState drains the shield first, so RemainingHp does not move while it holds), and
        // every placement inside the shielded stretch scores identically. The shape is read from
        // the curated ExecuteType, exactly as §6.2's table prescribes; no new per-skill field.
        //
        // The reported total is UNCAPPED, deliberately diverging from §6.2's literal
        // `D_max = min(H0, ...)`: the number this is compared against on the card is
        // DamageCalcResult.TotalDamage, which is itself uncapped (Simulate accumulates the killing
        // node's full damage). Capping one side and not the other would put two different
        // quantities next to each other.
        var others = PlaceGroupAt(nodes, groups, varianceGroup, -1);
        var otherSizes = new List<int>(groups.Count);
        for (int g = 0; g < groups.Count; g++) if (g != varianceGroup) otherSizes.Add(groups[g].Count);

        bool laterIsBetter = varianceShape is ExecuteType.MissingHp or ExecuteType.BaseWithMissingHpBonus;
        int best = varianceGroup;
        double bestTotal = double.NegativeInfinity;
        for (int j = 0, prefixLen = 0; j < groups.Count; j++)
        {
            // Placement j puts the variance group after the first j "other" groups.
            if (Simulate(others.GetRange(0, prefixLen), attacker, defender).RemainingHP <= 0)
                break; // dead here, and at every later placement too (prefix damage only grows)

            double total = Simulate(
                PlaceGroupAt(nodes, groups, varianceGroup, j), attacker, defender).TotalDamage;
            if (total > bestTotal || (laterIsBetter && total >= bestTotal)) { bestTotal = total; best = j; }

            if (j < otherSizes.Count) prefixLen += otherSizes[j];
        }
        if (double.IsNegativeInfinity(bestTotal)) bestTotal = authored; // no alive placement at all

        // Degenerate case (§6.2): does the combo already kill WITHOUT the variance group? Checked
        // on the Blend track for the same reason the placement is (above), via RemainingHP == 0
        // (SimResult.IsLethal reports the Max track, a different question).
        bool killsWithoutVariance = Simulate(others, attacker, defender).RemainingHP <= 0;

        return new PlacementSearchResult(
            killsWithoutVariance ? PlacementStatus.VarianceUnnecessary : PlacementStatus.Optimized,
            varianceGroup, best, authored, bestTotal);
    }

    /// <summary>Collapses <paramref name="groupOfNode"/> into contiguous (start, count) runs.
    /// A null/mis-sized map degrades to "every node is its own group" — the correct behavior for
    /// any caller that has no cast-grouping information, never an exception.</summary>
    private static List<(int Start, int Count)> BuildPlacementGroups(int nodeCount, IReadOnlyList<int>? groupOfNode)
    {
        var groups = new List<(int Start, int Count)>();
        if (groupOfNode is null || groupOfNode.Count != nodeCount)
        {
            for (int i = 0; i < nodeCount; i++) groups.Add((i, 1));
            return groups;
        }

        int runStart = 0;
        for (int i = 1; i <= nodeCount; i++)
        {
            if (i < nodeCount && groupOfNode[i] == groupOfNode[i - 1]) continue;
            groups.Add((runStart, i - runStart));
            runStart = i;
        }
        return groups;
    }

    /// <summary>Rebuilds the node list with group <paramref name="moved"/> lifted out and
    /// re-inserted at position <paramref name="toIndex"/> (in the remaining groups' coordinates);
    /// <paramref name="toIndex"/> &lt; 0 omits it entirely (used for the "does it kill without the
    /// variance node" probe). Every other group keeps its authored relative order.</summary>
    private static List<ComboNode> PlaceGroupAt(
        IReadOnlyList<ComboNode> nodes, List<(int Start, int Count)> groups, int moved, int toIndex)
    {
        var others = new List<int>(groups.Count);
        for (int g = 0; g < groups.Count; g++) if (g != moved) others.Add(g);

        var result = new List<ComboNode>(nodes.Count);
        void Append(int g)
        {
            var (start, count) = groups[g];
            for (int i = start; i < start + count; i++) result.Add(nodes[i]);
        }

        for (int p = 0; p <= others.Count; p++)
        {
            if (p == toIndex) Append(moved);
            if (p < others.Count) Append(others[p]);
        }
        return result;
    }

    private readonly record struct SimResult(
        double TotalDamage,
        List<(string NodeId, double Damage)> Breakdown,
        double RemainingHP,
        bool IsLethal,
        double LifestealHeal,
        double TotalDamageMin,
        double RemainingHPMin,
        double TotalDamageMax,
        double RemainingHPMax);
}

public enum DamageType { Physical, Magic, True }

/// <summary>Execute condition types per M05 Step 5, plus one item-proc-specific addition.
/// None = no execute logic applies to this node (ordinary damage node).
/// <see cref="BaseWithMissingHpBonus"/> was added for Kraken Slayer's real "Bring It Down"
/// formula (base × (1 + scalar × target's missing-health fraction)) — a different shape
/// from <see cref="MissingHp"/>'s additive fraction×absoluteMissingHP term, so it needed
/// its own case rather than reusing MissingHp (see the Simulate switch's doc comment).</summary>
public enum ExecuteType { None, Threshold, CurrentHp, MissingHp, BaseWithMissingHpBonus }

/// <summary>
/// One node of a combo sequence. Per the ambiguity resolution documented in the M05
/// agent report: M03 (Combo Engine) doesn't exist yet, so this carries already-resolved
/// <see cref="RatioAD"/>/<see cref="RatioAP"/>/<see cref="Damage"/> values, matching the
/// M05 spec's own Step 1 pseudocode literally. A future M03 is responsible for
/// populating these (e.g. via M11's FormulaInterpreter) — DamageEngine itself never
/// looks up a skill coefficient from a champion id.
/// </summary>
/// <param name="NodeId">Identifier for this node, used in <see cref="DamageCalcResult.Breakdown"/>.</param>
/// <param name="Damage">Flat bonus damage component.</param>
/// <param name="RatioAD">AD ratio (applies to attacker.Ad + attacker.BonusAD).</param>
/// <param name="RatioAP">AP ratio (applies to attacker.Ap).</param>
/// <param name="DamageType">Mitigation category for Step 2.</param>
/// <param name="CanCrit">Whether Step 4's critical multiplier applies to this node
/// (caller-determined — e.g. true for an auto-attack node, false for most ability
/// nodes). Not in the spec's literal Data Model list; added because Step 4 requires a
/// per-node crit decision and only the caller building the node knows which nodes can
/// crit.</param>
/// <param name="ExecuteType">Which of the 3 Step 5 execute branches (if any) applies.</param>
/// <param name="ExecuteThreshold">Flat HP value used by <see cref="ExecuteType.Threshold"/>
/// ("remainingHP &lt;= executeThreshold" per spec).</param>
/// <param name="ExecutePercent">Percentage (0-1) used by <see cref="ExecuteType.CurrentHp"/>
/// (percent of HP remaining at cast time) and <see cref="ExecuteType.MissingHp"/>
/// (percent of HP missing at cast time). Not in the spec's literal Data Model list;
/// added because both execute types are percentage-of-HP based and the spec doesn't
/// name a field for that percentage.</param>
/// <param name="CritDamageScalar">(M27) Fraction of a full crit's bonus this node deals when it
/// crits — 1.0 (default) is a full/normal crit (every pre-existing AA node), e.g. 0.40 for Garen E
/// ("crits for 40% of a standard crit's bonus"). Only read when <see cref="CanCrit"/> is true; see
/// <see cref="DamageEngine.CritFactor"/>.</param>
public sealed record ComboNode(
    string NodeId,
    double Damage,
    double RatioAD,
    double RatioAP,
    DamageType DamageType,
    bool CanCrit = false,
    ExecuteType ExecuteType = ExecuteType.None,
    double ExecuteThreshold = 0,
    double ExecutePercent = 0,
    double CritDamageScalar = 1.0,
    // (§27 (B), Ekko W passive) Dynamic running-HP gate: when set, this node contributes damage ONLY
    // on a track whose RUNNING RemainingHp is at/below this fraction of MaxHP at the moment it lands —
    // i.e. a low-HP-only on-hit that turns on partway through a combo as the (per-track hypothetical)
    // HP depletes below the threshold. Evaluated per-track so min/max diverge correctly. Null (default)
    // = ungated, byte-identical to every pre-existing node.
    double? HpBelowGateFraction = null);

/// <summary>
/// Attacker's live stats. The four penetration fields are caller-supplied truth (the
/// player's own public in-game stats — P1). Percent values are the FRACTION of the
/// target's resistance ignored (0.30 = 30% pen); flat values are the raw resistance
/// points ignored (armor pen / lethality, magic pen). All default to 0.
/// </summary>
/// <param name="CritDamageMultiplier">Real crit-damage multiplier (e.g. 2.0 = 200%
/// total damage on crit) from the Live Client API's <c>championStats.critDamage</c>
/// (base 175% + any crit-damage-boosting items, e.g. Infinity Edge). 0 means "not
/// reported" (API absent/older shape) — <see cref="DamageEngine"/> falls back to its
/// own <see cref="DamageEngine.CritDamageMultiplier"/> constant in that case rather
/// than treating 0 as a real "0x on crit" value. Default 0 keeps every pre-existing
/// call site compiling unchanged (additive field).</param>
public sealed record AttackerStat(
    double Ad,
    double BonusAD,
    double Ap,
    int Level,
    double CriticalChance,
    double LifeSteal,
    double ArmorPenFlat = 0,
    double ArmorPenPercent = 0,
    double MagicPenFlat = 0,
    double MagicPenPercent = 0,
    double CritDamageMultiplier = 0);

/// <summary>
/// Defender's live stats. Per M05's "DefenderStat 산출 원칙", Armor/Mr must already be
/// the specific targeted opponent's real value (base + confirmed item/rune bonus) —
/// DamageEngine treats this as caller-supplied truth and applies no default/estimate
/// of its own (Policy Compliance Checklist; see M05 agent report Policy Self-Check for
/// the explicit boundary statement).
/// </summary>
public sealed record DefenderStat(
    double CurrentHP,
    double MaxHP,
    double Armor,
    double Mr,
    double Shield,
    double ArmorReductionFlat = 0,
    double ArmorReductionPercent = 0,
    double MrReductionFlat = 0,
    double MrReductionPercent = 0);

/// <summary>
/// Minimal placeholder input type for M06 Rune Engine output. M06 doesn't exist yet
/// (comes after M05 in build order) and hasn't published RuneEffect's real shape.
/// M05's own 6-step Internal Logic never references rune effects at all, so this field
/// is accepted (to satisfy the Interfaces contract) but not applied by the pipeline —
/// see M05 agent report Notes for Reviewer.
/// </summary>
public sealed record RuneEffect(string Id);

public sealed record DamageCalcInput(
    IReadOnlyList<ComboNode> Nodes,
    AttackerStat Attacker,
    DefenderStat Defender,
    IReadOnlyList<RuneEffect>? ActiveRunes = null);

public sealed record NodeDamage(string NodeId, double Damage);

/// <summary>
/// <see cref="LifestealHeal"/> is not listed in the M05 spec's literal Data Model
/// section for DamageCalcResult, but Internal Logic Step 4 explicitly requires
/// lifesteal to be "a separate field" of attacker healing that never feeds back into
/// the damage total — that instruction is unambiguous even though the Data Model list
/// omits it, so the field is added here as the minimum needed to satisfy Step 4. See
/// M05 agent report Notes for Reviewer.
///
/// <para><see cref="TotalDamageMin"/>/<see cref="TotalDamageMax"/>/<see cref="RemainingHPMin"/>/
/// <see cref="RemainingHPMax"/> (v2.8, user-requested simplification): a genuine min~max
/// damage RANGE computed via two parallel remainingHp tracks (see <c>DamageEngine.Simulate</c>'s
/// doc comment) — Min assumes no <see cref="Damage.ComboNode.CanCrit"/> node ever crits, Max
/// assumes every crit-capable node with nonzero <see cref="AttackerStat.CriticalChance"/> crits
/// with certainty. <see cref="TotalDamage"/>/<see cref="RemainingHP"/> are UNCHANGED (the
/// pre-existing probability-weighted expected-value blend) and kept for any consumer that still
/// wants a single number — purely additive, no existing caller needs to change.</para>
///
/// <para><see cref="IsLethal"/>'s semantics CHANGED (v2.8): it now reports the MAX (best-case,
/// all-crits-landing) track's terminal executed state, not the blend track's. "처치 가능!"
/// (kill POSSIBLE) is therefore honest about being a best-case indicator, never a guaranteed-kill
/// claim — see the HUD's label text, which already says "가능" (possible) not "확정" (certain).
/// When no node in the sequence can crit (or CriticalChance is 0), Max collapses to the same
/// value the blend track would produce, so this is backward-compatible for every non-crit combo.</para>
/// </summary>
public sealed record DamageCalcResult(
    double TotalDamage,
    IReadOnlyList<NodeDamage> Breakdown,
    double RemainingHP,
    bool IsLethal,
    double KillThresholdHP,
    double LifestealHeal,
    double TotalDamageMin = 0,
    double TotalDamageMax = 0,
    double RemainingHPMin = 0,
    double RemainingHPMax = 0);
