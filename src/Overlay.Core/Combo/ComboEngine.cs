using System.Text.Json;
using System.Text.Json.Serialization;
using Overlay.Core.ChampionDb;
using Overlay.Core.Damage;
using Overlay.Core.Items;
using Overlay.Core.Runes;

namespace Overlay.Core.Combo;

/// <summary>
/// M03 Combo Engine: builds/executes/(de)serializes a champion-agnostic combo sequence
/// (docs/modules/M03_COMBO_ENGINE.md), and is the module responsible for assembling the
/// *real* <see cref="DamageCalcInput"/> — calling <see cref="RuneEngine.GetActiveEffects"/>
/// and feeding the result toward <see cref="DamageEngine.Calculate"/> — resolving the
/// integration gap both the M05 and M06 agent reports flagged as "expected to be
/// resolved by M03" (see Notes for Reviewer in the M03 agent report for the full
/// writeup of every reconciliation decision below).
///
/// No champion-name/id branching anywhere in this file — champion-specific stack/
/// condition data is looked up generically via <see cref="ChampionRepository.GetSpecialProperty"/>
/// (M03 Agent Implementation Notes).
///
/// No automatic key input of any kind — <see cref="ComboResult"/> is inert
/// display/decision-support data only (Policy Compliance Checklist item (a)).
/// </summary>
public sealed class ComboEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DamageEngine _damageEngine;
    private readonly RuneEngine _runeEngine;

    public ComboEngine(DamageEngine damageEngine, RuneEngine runeEngine)
    {
        _damageEngine = damageEngine ?? throw new ArgumentNullException(nameof(damageEngine));
        _runeEngine = runeEngine ?? throw new ArgumentNullException(nameof(runeEngine));
    }

    // ---------------------------------------------------------------------------------
    // Auto-Trigger Rune Damage Engine: 6 auto-trackable runes (RuneRepository classifies
    // them ApiTrackable:true, unlike the 8 manual runes below) whose damage bonus is only
    // applied when the EXECUTED node sequence genuinely satisfies that rune's real trigger
    // condition -- see ApplyAxiomArcanistUltimateMultiplier/ApplyRemainingAutoTriggeredRunes
    // and rune_effects.json's per-id "_note" for the full reasoning/citations, and
    // docs/reports/agent/auto_rune_trigger_engine_report.md for worked numeric examples.
    // Distinct from the 8 MANUAL runes' runeBonusNodes block further down in Execute(),
    // which stays a conservative "equipped + checkbox on -> always one extra hit"
    // approximation (unchanged by this feature) because those 8 have no live trigger-
    // condition signal at all -- these 6 do (their trigger is derivable from the combo's
    // own node sequence, which this module already has full visibility into).
    // ---------------------------------------------------------------------------------
    private const string AxiomArcanistRuneId = "8224";
    private const string SummonAeryRuneId = "8214";
    private const string ElectrocuteRuneId = "8112";
    private const string HailOfBladesRuneId = "9923";
    private const string DarkHarvestRuneId = "8128";
    private const string ConquerorRuneId = "8010";

    /// <summary>The 6 API-"trackable" runes this engine auto-triggers based on the combo's own
    /// executed node sequence (Axiom Arcanist / Summon Aery / Electrocute / Hail of Blades /
    /// Dark Harvest / Conqueror). Distinct from the 8 manual runes in
    /// <see cref="ChampionDb.RuneApiTrackability.NonTrackableRuneIds"/>. Exposed as raw Data Dragon
    /// perk ids so <see cref="ComboRunner.LoadRuneSelectionAndArmManualFlags"/> can put a player's
    /// genuinely-equipped auto-trigger runes into <see cref="ExecutionContext.UserRuneConfig"/>'s
    /// SelectedRuneIds — WITHOUT this, an equipped auto-trigger rune never reached
    /// <see cref="ApplyRemainingAutoTriggeredRunes"/>/<see cref="ApplyAxiomArcanistUltimateMultiplier"/>'s
    /// <c>SelectedRuneIds.Contains(...)</c> gate and silently never fired in a live game (loop 46's
    /// engine was effectively dormant). Each rune's actual per-combo trigger CONDITION is still
    /// checked inside those two methods; membership here only means "the player has it on their page".</summary>
    // (M23 Phase 2 Step 5) Taxonomy classification — Source=Rune for every id below, per
    // docs/modules/M23_EFFECT_CATALOG.md's archetype registry (A4 Self x SelfBuffActive unless
    // noted). This is ANNOTATION ONLY: none of these runes move into ComboRunner's Stage A
    // (per-node) pass. They stay in ComboEngine.Execute's Stage B (result-mutation) because each
    // one legitimately operates on the EXECUTED RESULT, not an appendable pre-execute hit:
    //   - Conqueror (8010, A4): heal ramps off the running/total combo damage (ApplyRemainingAutoTriggeredRunes) — no single node "is" the heal.
    //   - Axiom Arcanist (8224, A4): multiplies the ultimate NODE IN PLACE (ApplyAxiomArcanistUltimateMultiplier) — not a new appended hit.
    //   - Electrocute (8112, A6 OnBasicAttack x EveryNth-style "3rd unique hit"): fires only after a 3-damaging-node threshold within the executed sequence (ApplyRemainingAutoTriggeredRunes) — the threshold spans the WHOLE combo, not a single node's own trigger.
    //   - Summon Aery (8214) / Hail of Blades (9923) / Dark Harvest (8128) (A4-adjacent, gated on "at least one damaging node"): same whole-sequence gate as Electrocute.
    // Forcing any of these into Stage A (ComboRunner.ApplyBinDamage) WOULD change numbers — see
    // M23_PHASE2_REFACTOR_SPEC.md's Target section ("Stage B (runes): DO NOT move into Stage A").
    public static readonly IReadOnlySet<int> AutoTriggeredRuneIds = new HashSet<int>
    {
        8224, // Axiom Arcanist
        8214, // Summon Aery
        8112, // Electrocute
        9923, // Hail of Blades
        8128, // Dark Harvest
        8010, // Conqueror
    };

    /// <summary>(loop 170) Manual (non-API-trackable) runes whose real trigger is "dealing ABILITY
    /// damage to a champion" (per each id's rune_effects.json _note / live wiki). These are the only
    /// manual runes whose trigger this app can AUTO-VERIFY from the executed combo — their bonus is
    /// applied ONLY when the combo actually contains a cast ability node (<see cref="ComboNodeType.Skill"/>),
    /// gated in <see cref="Execute"/>'s manual-rune block. The other 4 manual runes' triggers
    /// (Cheap Shot 8126 = target crowd-controlled, Sudden Impact 8143 = post-dash/leap/blink/stealth,
    /// Shield Bash 8401 = just gained a shield, Grasp 8437 = melee in-combat stacks) have NO combo/API
    /// signal, so they are never auto-armed from mere equip — they require an explicit user opt-in
    /// (persisted manual flag); see <see cref="ComboRunner.LoadRuneSelectionAndArmManualFlags"/>.</summary>
    public static readonly IReadOnlySet<int> AbilityDamageTriggeredManualRuneIds = new HashSet<int>
    {
        8237, // Scorch      — "Dealing ability damage to an enemy champion sets them on fire..."
        8229, // Arcane Comet — "Dealing ability damage or pet damage to an enemy champion..."
        8992, // Deathfire Touch — "Dealing ability damage ... inflicts a burn..."
    };

    /// <summary>(loop 174) Manual runes whose trigger is "after a dash / leap / blink / teleport /
    /// stealth-exit". This app can't see champion movement, so the user signals it by placing a Flash
    /// (점멸) summoner node in the combo — <see cref="Execute"/> then treats the dash condition as met
    /// and applies these runes (otherwise they stay gated, per §28). Currently just Sudden Impact.</summary>
    public static readonly IReadOnlySet<int> DashTriggeredManualRuneIds = new HashSet<int>
    {
        8143, // Sudden Impact — "...after using a dash, leap, blink, teleport, or when leaving stealth for 4s."
    };

    /// <summary>(M24 P4) CONDITIONAL damage-AMPLIFIER runes: a ×(1+amp) multiplier on the whole combo's
    /// post-mitigation damage that applies only when the rune's condition holds. Keyed by Data Dragon
    /// perk id → the BEST-CASE amp fraction (condition met). Because the condition (target/caster HP,
    /// first-hit-in-combat) is not reliably observable, an equipped amplifier only lifts the uncertainty
    /// range's CEILING — ComboRunner multiplies RangeMax by (1+amp) while RangeMin/TotalDamage stay
    /// unamplified (the conservative floor). Values from wiki.leagueoflegends.com (M24 Appendix A2,
    /// current patch), re-check on patch changes:
    ///   8014 Coup de Grace (target &lt;40% HP), 8017 Cut Down (target &gt;60% max HP), 8299 Last Stand
    ///   (caster &lt;60% HP; max at 30%), 8005 Press the Attack (post-3-hit window; the 40-160 added hit is
    ///   deferred), 8369 First Strike (first damage in combat, vs champions).
    /// NOTE: Last Stand's caster-HP condition IS API-observable — a future refinement can auto-resolve it
    /// rather than treat it as a pure ceiling; kept simple (ceiling-only) here.</summary>
    public static readonly IReadOnlyDictionary<int, double> ConditionalAmplifierRuneAmps = new Dictionary<int, double>
    {
        [8014] = 0.08, // Coup de Grace
        [8017] = 0.08, // Cut Down
        [8005] = 0.08, // Press the Attack (amp portion)
        [8369] = 0.07, // First Strike
    };

    /// <summary>(M24 P4) Last Stand (8299): ×(1+amp) where amp scales 0.05→0.11 as the CASTER drops
    /// from 60% to 30% HP (0 above 60%, capped 0.11 at/below 30%). Unlike the runes above, its
    /// condition (the caster's own HP) IS observable via <see cref="ActivePlayerStats.CurrentHealth"/>/
    /// <c>MaxHealth</c>, so ComboRunner AUTO-RESOLVES the actual amp from live HP for the range ceiling
    /// rather than always assuming the max. Returns 0 when the caster is healthy or HP is unknown.</summary>
    public const int LastStandRuneId = 8299;

    /// <summary>Live Last Stand amp for a caster at <paramref name="currentHp"/>/<paramref name="maxHp"/>
    /// (see <see cref="LastStandRuneId"/>). 0 above 60% HP, linear 0.05→0.11 across 60%→30%, 0.11 below.</summary>
    public static double LastStandAmp(double currentHp, double maxHp)
    {
        if (maxHp <= 0) return 0;
        double pct = currentHp / maxHp;
        if (pct >= 0.60) return 0;
        if (pct <= 0.30) return 0.11;
        return 0.05 + (0.60 - pct) / (0.60 - 0.30) * (0.11 - 0.05);
    }

    /// <summary>(M24 P5) ENEMY DEFENSIVE runes → the flat Armor/MR/Shield each grants, making the
    /// target tankier (LESS damage) — so they lower the uncertainty range's FLOOR (RangeMin). The
    /// enemy's runes are NOT API-observable, so this is a USER KNOB (the assumed-enemy-rune list);
    /// unset = none = the current best-case (Max-favorable) behavior, so the resolved result is
    /// unchanged. Values from wiki.leagueoflegends.com (M24 Appendix A3), representative:
    ///   8429 Conditioning (+8 armor/MR flat; the +3% is omitted), 8439 Aftershock (ASSUMES the
    ///   target is CC'd → +45 armor/MR), 8465 Guardian (~100 shield; scales with AP in-game).
    /// Bone Plating (per-hit flat reduction) and Overgrowth (max HP) aren't flat resist/shield and
    /// are deferred.</summary>
    public static readonly IReadOnlyDictionary<int, (double Armor, double Mr, double Shield)> DefensiveRuneResists =
        new Dictionary<int, (double, double, double)>
        {
            [8429] = (8, 8, 0),    // Conditioning
            [8439] = (45, 45, 0),  // Aftershock (target CC'd)
            [8465] = (0, 0, 100),  // Guardian (shield)
        };

    /// <summary>Returns <paramref name="defender"/> with the summed Armor/MR/Shield of the assumed
    /// enemy defensive <paramref name="runeIds"/> added (see <see cref="DefensiveRuneResists"/>).
    /// Unknown ids are ignored; an empty list returns an equivalent defender (used for RangeMin).</summary>
    public static Damage.DefenderStat ApplyDefensiveRunes(Damage.DefenderStat defender, IEnumerable<int> runeIds)
    {
        double armor = 0, mr = 0, shield = 0;
        foreach (var id in runeIds)
            if (DefensiveRuneResists.TryGetValue(id, out var b)) { armor += b.Armor; mr += b.Mr; shield += b.Shield; }
        return defender with { Armor = defender.Armor + armor, Mr = defender.Mr + mr, Shield = defender.Shield + shield };
    }

    /// <summary>Axiom Arcanist (rune_effects.json "8224"): "Your Ultimate has 12% increased
    /// damage, healing, and shielding. (AoE damage is reduced to a 8% increase)." Which of the
    /// two applies is resolved per-champion via the M21 "AoeUltimate" R-slot tag -- see
    /// <see cref="ApplyAxiomArcanistUltimateMultiplier"/>.</summary>
    private const double AxiomArcanistSingleTargetMultiplier = 1.12;
    private const double AxiomArcanistAoeMultiplier = 1.08;

    /// <summary>Conqueror (rune_effects.json "8010"): "heal for 8% of the damage you deal to
    /// champions (5% for ranged champions)" once fully stacked -- see
    /// <see cref="ApplyRemainingAutoTriggeredRunes"/>.</summary>
    private const double ConquerorMeleeHealPercent = 0.08;
    private const double ConquerorRangedHealPercent = 0.05;

    /// <summary>A node "deals damage" (counts toward Electrocute/Conqueror's hit-counting and
    /// Summon Aery/Dark Harvest's "at least one damaging node" gate) when it actually
    /// contributes damage on its own: a nonzero flat <see cref="ComboNode.Damage"/> or any
    /// nonzero AD/AP ratio. Operates on the raw M03 <see cref="ComboNode"/> (not the M05-
    /// folded <see cref="Damage.ComboNode"/>, which has no separate RatioBonusAD field).</summary>
    private static bool IsDamagingNode(ComboNode node)
        => node.Damage > 0 || node.RatioAD > 0 || node.RatioBonusAD > 0 || node.RatioAP > 0;

    /// <summary>
    /// Validates <paramref name="nodeDefinitions"/> (non-empty, unique non-empty ids,
    /// defined enum values) and wraps them into a <see cref="ComboGraph"/>. MVP builds
    /// the graph as the linear array order the caller supplied (spec's own design
    /// note); an implicit chain of <see cref="ComboEdge"/>s (node[i] -> node[i+1]) is
    /// also recorded so a future V2 branching traversal has a real edge list to extend
    /// rather than a rewrite — this implicit chain is acyclic by construction, so no
    /// cycle-detection algorithm is implemented yet (nothing to detect until V2
    /// introduces caller-supplied branch edges; see M03 report Notes for Reviewer).
    /// </summary>
    public ComboGraph BuildGraph(IReadOnlyList<ComboNode> nodeDefinitions)
    {
        ArgumentNullException.ThrowIfNull(nodeDefinitions);
        if (nodeDefinitions.Count == 0)
            throw new ArgumentException("nodeDefinitions must not be empty", nameof(nodeDefinitions));

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodeDefinitions)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                throw new ArgumentException("ComboNode.Id is required");
            if (!seenIds.Add(node.Id))
                throw new ArgumentException($"Duplicate ComboNode.Id '{node.Id}'");
            if (!Enum.IsDefined(node.NodeType))
                throw new ArgumentException($"Unknown NodeType on node '{node.Id}'");
            if (!Enum.IsDefined(node.DamageType))
                throw new ArgumentException($"Unknown DamageType on node '{node.Id}'");
            if (!Enum.IsDefined(node.ExecuteType))
                throw new ArgumentException($"Unknown ExecuteType on node '{node.Id}'");
        }

        var edges = new List<ComboEdge>(Math.Max(0, nodeDefinitions.Count - 1));
        for (int i = 0; i < nodeDefinitions.Count - 1; i++)
            edges.Add(new ComboEdge(nodeDefinitions[i].Id, nodeDefinitions[i + 1].Id));

        return new ComboGraph(nodeDefinitions.ToArray(), edges);
    }

    /// <summary>
    /// Walks <paramref name="graph"/>'s nodes in order (spec Internal Logic #2):
    /// evaluates each node's condition, skips unmet ones (mana/damage unaffected),
    /// accumulates mana, calls <see cref="RuneEngine.GetActiveEffects"/> then
    /// <see cref="DamageEngine.Calculate"/> for the executed subsequence, and builds
    /// the final <see cref="ComboResult"/>.
    /// </summary>
    public ComboResult Execute(ComboGraph graph, ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(context);

        var executed = new List<ComboNode>(graph.Nodes.Count);
        var elapsedAtNode = new List<double>(graph.Nodes.Count);
        double totalMana = 0;
        double totalCastTime = 0;
        bool manaSufficient = true;

        foreach (var node in graph.Nodes)
        {
            if (!EvaluateCondition(node, context, totalMana))
            {
                // NOTE(M18 Logging, not built yet): a warning log call belongs here —
                // "combo node '{node.Id}' skipped: condition {node.Condition} unmet".
                if (node.Condition?.Type == ConditionType.ManaGte)
                    manaSufficient = false;
                continue;
            }

            executed.Add(node);
            totalMana += node.Mana;
            totalCastTime += node.CastTime + node.Delay;
            // Snapshot of totalCastTime right after this node lands — this node's approximate
            // elapsed-time-in-combo timestamp, 1:1 aligned with executed/damageNodes. Only
            // consumer today is ApplyAttachedItemEffects's damage-amplification window (see its
            // doc comment); an approximation (ignores TravelTime/real animation timing)
            // consistent with this project's existing linear-combo-timeline conventions.
            elapsedAtNode.Add(totalCastTime);
        }

        // Auto-Trigger Rune #1/6: Axiom Arcanist mutates `executed` in place, BEFORE
        // BuildDamageNode's fold, so its multiplier applies uniformly to every field that
        // feeds the ultimate node's damage (see method doc comment).
        ApplyAxiomArcanistUltimateMultiplier(executed, context);

        var casterStats = new RuneCasterStats(
            Level: context.Attacker.Level,
            BonusAd: context.Attacker.BonusAD,
            Ap: context.Attacker.Ap,
            MaxHealth: context.CasterMaxHealth,
            IsMelee: context.CasterIsMelee);
        var activeRuneEffects = _runeEngine.GetActiveEffects(context.ChampionId, context.UserRuneConfig, casterStats);
        // Adapter into M05's still-placeholder Damage.RuneEffect(string Id) — see
        // Notes for Reviewer for why DamageEngine.cs itself is not touched.
        var damageActiveRunes = activeRuneEffects.Select(r => new Damage.RuneEffect(r.RuneId)).ToArray();

        var damageNodes = executed.Select(n => BuildDamageNode(n, context)).ToList();

        // Auto-Trigger Runes #2-6/6: Summon Aery / Electrocute / Hail of Blades / Dark Harvest /
        // Conqueror. Each is only applied when the user selected it AND the executed sequence
        // genuinely satisfies its real trigger condition (see method doc comment). Runs BEFORE
        // the manual-rune block below so autoRuneBonusNodes lands in the trailing region ahead of
        // runeBonusNodes -- damageNodes[0..executed.Count) stays an exact 1:1 projection of
        // executed either way (this step only appends trailing nodes / mutates existing Damage
        // values in place, never inserts/removes at executed's own indices).
        var (autoRuneBonusNodes, conquerorHealPercent) =
            ApplyRemainingAutoTriggeredRunes(executed, damageNodes, context, casterStats);
        damageNodes.AddRange(autoRuneBonusNodes);

        // Manual, node-attached active-item effects (ComboNode.AttachedItemId, e.g. Deathfire
        // Grasp) — independent of the rune engine above. The item's own burst becomes a NEW
        // trailing node (its own damage type, e.g. Magic, may differ from whatever type the node
        // it's attached to already has, so it is never folded into that node's own Damage — see
        // method doc comment); the amplify-window component DOES mutate damageNodes[0..executed.Count)
        // in place (scaling, never changing type, so no type-mixing risk there). Appended here,
        // between autoRuneBonusNodes and runeBonusNodes, so the breakdown offsets below stay simple.
        var attachedItemBurstNodes = ApplyAttachedItemEffects(executed, elapsedAtNode, damageNodes, context);
        damageNodes.AddRange(attachedItemBurstNodes);

        // A manual rune with a real DamageBonus (RuneEngine + RuneEffectDb resolved one, i.e. the
        // rune is user-activated AND has a known damage formula) becomes ONE additional hit for the
        // whole combo — the exact "additive extra hit" pattern ComboRunner.AppendBonusHits already
        // uses for champion on-hit/on-ability passives, just applied once per combo per active rune
        // rather than once per node, since a rune proc isn't tied to a specific combo node. This is
        // a conservative, documented approximation (real proc timing/cooldowns are not modeled),
        // consistent with the project's existing "onHit approximated as every attack" precedent.
        var runeBonusNodes = new List<Damage.ComboNode>();
        // (loop 170) A manual rune whose trigger is "dealing ability damage" only fires when the
        // executed combo actually casts an ability (Skill node) — a basic-attack-only combo must NOT
        // get Scorch/Comet/Deathfire. Fixes the user-reported bug where an AA-only Ahri combo picked up
        // rune#8237 (Scorch) despite no ability being cast. The other manual runes' triggers can't be
        // combo-verified and are gated upstream by not being auto-armed (ComboRunner arming).
        bool comboHasAbility = executed.Any(n => n.NodeType == ComboNodeType.Skill);
        // (loop 174) A Flash (점멸) summoner node is the user's explicit "I dashed/blinked here" signal,
        // satisfying the dash-trigger runes' condition (Sudden Impact). No Flash node → those stay gated.
        bool comboHasDash = executed.Any(n => n.NodeType == ComboNodeType.Summoner
            && string.Equals(n.Name, "Flash", StringComparison.OrdinalIgnoreCase));
        foreach (var rune in activeRuneEffects)
        {
            if (rune.DamageBonus is not > 0 || rune.DamageType is not { } type) continue;
            if (int.TryParse(rune.RuneId, out int rmid))
            {
                // ability-trigger rune, but this combo cast no ability → condition unmet
                if (AbilityDamageTriggeredManualRuneIds.Contains(rmid) && !comboHasAbility) continue;
                // dash-trigger rune (Sudden Impact), but no Flash/dash node in the combo → condition unmet
                if (DashTriggeredManualRuneIds.Contains(rmid) && !comboHasDash) continue;
            }
            runeBonusNodes.Add(new Damage.ComboNode(
                NodeId: $"rune#{rune.RuneId}",
                Damage: rune.DamageBonus.Value,
                RatioAD: 0,
                RatioAP: 0,
                DamageType: MapRuneDamageType(type)));
        }
        damageNodes.AddRange(runeBonusNodes);

        // Part 3 (loop 50): per-node FORCE-APPLIED runes (ComboNode.AttachedRuneId). Unlike every
        // rune path above (all gated on the player REALLY having the rune equipped and its trigger
        // genuinely met), an attached rune is theorycraft — applied to its node unconditionally.
        // Additive runes append trailing bonus nodes; Axiom Arcanist scales its attached node in
        // place. Runs after all auto/manual rune nodes so index-alignment of damageNodes[0..executed)
        // is preserved for the suffix-threshold loop below (same as the item-burst step's contract).
        var attachedRuneBonusNodes = ApplyAttachedRuneEffects(executed, damageNodes, context, casterStats);
        damageNodes.AddRange(attachedRuneBonusNodes);

        var damageInput = new DamageCalcInput(damageNodes, context.Attacker, context.Defender, damageActiveRunes);
        var damageResult = _damageEngine.Calculate(damageInput);

        // M20 §6 item 3 / §6.3: the suffix kill-threshold for the "finisher" node — the LAST
        // executed node whose ExecuteType is HP-scaling (MissingHp/CurrentHp/BaseWithMissingHpBonus,
        // e.g. Garen R) — answers "how much HP must the target be at when THIS node is cast for the
        // rest of the combo to kill". Same damageNodes/context KillThresholdHP above already used,
        // just scoped to that node's own suffix instead of the whole sequence — executed[i] and
        // damageNodes[i] stay index-aligned because damageNodes is built as a 1:1 projection of
        // executed BEFORE the trailing runeBonusNodes are appended (see damageNodes.AddRange above).
        // Null when the combo has no such node (nothing HP-scaling-dependent to show — the whole-
        // combo KillThresholdHP is already the complete story), or when the fold can't apply to that
        // suffix (Threshold node / nonzero shield — see DamageEngine.Fold's doc comment).
        // SolveSuffixThresholdHp has no bisection fallback, so this is simply left unpopulated in
        // that case rather than guessed at.
        string? finisherNodeLabel = null;
        double? suffixThresholdHp = null;
        for (int i = executed.Count - 1; i >= 0; i--)
        {
            if (executed[i].ExecuteType is not (ComboExecuteType.MissingHp or ComboExecuteType.CurrentHp or ComboExecuteType.BaseWithMissingHpBonus))
                continue;

            double? suffix = DamageEngine.SolveSuffixThresholdHp(
                damageNodes, i, context.Attacker, context.Defender, DamageEngine.CritTrack.Max);
            if (suffix is { } s)
            {
                suffixThresholdHp = Math.Round(s, 2);
                finisherNodeLabel = FinisherLabel(executed[i]);
            }
            break; // only the LAST such node matters, whether or not the fold could solve it
        }

        // M20 §6.2 (CLAUDE_CODE_TODO §75): the kill-feasible MAXIMUM total damage. The headline
        // above is whatever the AUTHORED node order produced, and for a variance-heavy node that
        // understates the realizable burst by a large factor (Garen R rank 1 spans 125→375 on a
        // 1000-HP target purely by placement). DamageEngine re-runs its own pipeline once per
        // placement of the single variance CAST GROUP and reports the best — see
        // SolveKillFeasibleMaxDamage's doc comment for the alive constraint, the group-not-node
        // rule, and the Blend-decides-the-placement choice.
        var (groupOfNode, groupLabels) = BuildCastGroups(executed, damageNodes.Count);
        var placement = DamageEngine.SolveKillFeasibleMaxDamage(
            damageNodes, groupOfNode, context.Attacker, context.Defender);
        var (burstCeilingStatus, burstCeilingDamage, burstCeilingNodeLabel, burstCeilingSequence) =
            DescribePlacement(placement, groupLabels, damageResult.TotalDamage);

        var breakdown = new List<NodeBreakdownEntry>(
            executed.Count + autoRuneBonusNodes.Count + attachedItemBurstNodes.Count + runeBonusNodes.Count + attachedRuneBonusNodes.Count);
        for (int i = 0; i < executed.Count; i++)
        {
            breakdown.Add(new NodeBreakdownEntry(
                executed[i].Id,
                damageResult.Breakdown[i].Damage,
                executed[i].Delay));
        }
        // Auto-trigger rune bonus nodes were appended right after the executed nodes (before
        // attachedItemBurstNodes/runeBonusNodes), so their damageResult.Breakdown entries land at
        // this trailing offset.
        for (int j = 0; j < autoRuneBonusNodes.Count; j++)
        {
            var bd = damageResult.Breakdown[executed.Count + j];
            breakdown.Add(new NodeBreakdownEntry(bd.NodeId, bd.Damage, Delay: 0));
        }
        // Attached-item burst nodes were appended AFTER executed nodes AND autoRuneBonusNodes,
        // BEFORE runeBonusNodes, so their damageResult.Breakdown entries land at this offset.
        for (int j = 0; j < attachedItemBurstNodes.Count; j++)
        {
            var bd = damageResult.Breakdown[executed.Count + autoRuneBonusNodes.Count + j];
            breakdown.Add(new NodeBreakdownEntry(bd.NodeId, bd.Damage, Delay: 0));
        }
        // Rune bonus nodes were appended AFTER executed nodes, autoRuneBonusNodes, AND
        // attachedItemBurstNodes in damageNodes, so their damageResult.Breakdown entries land at
        // this further offset — index alignment holds.
        for (int j = 0; j < runeBonusNodes.Count; j++)
        {
            var bd = damageResult.Breakdown[executed.Count + autoRuneBonusNodes.Count + attachedItemBurstNodes.Count + j];
            breakdown.Add(new NodeBreakdownEntry(bd.NodeId, bd.Damage, Delay: 0));
        }
        // Attached-rune force-apply bonus nodes (Part 3, loop 50) were appended LAST in damageNodes
        // (after executed, autoRuneBonusNodes, attachedItemBurstNodes, AND runeBonusNodes — see the
        // damageNodes.AddRange chain above), so their damageResult.Breakdown entries land at this
        // final offset. Without this loop their damage still reaches TotalDamage but the "{nodeId}#rune{id}"
        // entry is dropped from NodeBreakdown.
        for (int j = 0; j < attachedRuneBonusNodes.Count; j++)
        {
            var bd = damageResult.Breakdown[executed.Count + autoRuneBonusNodes.Count + attachedItemBurstNodes.Count + runeBonusNodes.Count + j];
            breakdown.Add(new NodeBreakdownEntry(bd.NodeId, bd.Damage, Delay: 0));
        }

        // Auto-Trigger Rune #6b/6 (Conqueror's full-stack heal): computed AFTER the final
        // Calculate, off damageResult.TotalDamage (which already includes Conqueror's own
        // per-node ramping bonus baked into damageNodes above) — see
        // ApplyRemainingAutoTriggeredRunes's doc comment and ComboResult.RuneHeal's doc comment
        // for why this is a NEW field rather than reusing DamageCalcResult.LifestealHeal.
        double runeHeal = conquerorHealPercent is { } healPct
            ? Math.Round(healPct * damageResult.TotalDamage, 2)
            : 0;

        // (M24 P1) The UNIFIED uncertainty range the overlay draws as "[하한 ~ 상한]". Today only the
        // crit axis is modeled, so RangeMin/Max == CritMin/Max (the crit floor/ceiling); P2/P3/P4 fold
        // condition/knob/distance/amp axes into this range and widen it. Building it from the crit
        // endpoints (fallback = TotalDamage for a degenerate/empty result) keeps it pure-additive —
        // no existing damage number changes.
        var totalRange = DamageRange.FromCritRange(
            damageResult.TotalDamageMin, damageResult.TotalDamageMax, damageResult.TotalDamage);

        return new ComboResult(
            TotalDamage: damageResult.TotalDamage,
            TotalMana: totalMana,
            ManaSufficient: manaSufficient,
            KillThresholdHP: damageResult.KillThresholdHP,
            IsLethal: damageResult.IsLethal,
            NodeBreakdown: breakdown,
            TotalCastTime: totalCastTime,
            TotalDamageMin: damageResult.TotalDamageMin,
            TotalDamageMax: damageResult.TotalDamageMax,
            FinisherNodeLabel: finisherNodeLabel,
            SuffixThresholdHP: suffixThresholdHp,
            RuneHeal: runeHeal,
            RangeMin: totalRange.Min,
            RangeMax: totalRange.Max,
            BurstCeiling: burstCeilingStatus,
            BurstCeilingDamage: burstCeilingDamage,
            BurstCeilingNodeLabel: burstCeilingNodeLabel,
            BurstCeilingSequence: burstCeilingSequence);
    }

    /// <summary>
    /// (M20 §6.2) Maps each entry of the damage-node list to the AUTHORED cast it came from, and
    /// returns one display label per group. ComboRunner expands a curated skill into one damage
    /// node per hit/count with ids shaped <c>"{authoredId}#cast{c}h{h}c{n}"</c>
    /// (<see cref="ComboRunner"/>'s ExpandCuratedSkill), so the authored cast is the id up to
    /// <c>'#'</c> — the same id-convention knowledge <see cref="ApplyAxiomArcanistUltimateMultiplier"/>
    /// already relies on. Grouping by cast is required, not cosmetic: Garen R is curated as two
    /// hits that land simultaneously and moving them apart would let the missing-HP hit read its
    /// own sibling's flat damage as freshly-missing health (Garen.json <c>_noteR2</c>).
    ///
    /// <para>Everything past <paramref name="executedCount"/> (auto/manual rune bonus hits,
    /// attached item bursts) is lumped into ONE terminal group with a null label: those are
    /// "one extra hit for the whole combo" approximations with no real cast timing, so subdividing
    /// them would invent placement slots the player cannot act on.</para>
    /// </summary>
    private static (List<int> GroupOfNode, List<string?> GroupLabels) BuildCastGroups(
        List<ComboNode> executed, int totalNodeCount)
    {
        var groupOfNode = new List<int>(totalNodeCount);
        var labels = new List<string?>();
        string? prevKey = null;

        for (int i = 0; i < executed.Count; i++)
        {
            string id = executed[i].Id;
            int hash = id.IndexOf('#');
            string key = hash < 0 ? id : id[..hash];
            if (prevKey is null || !string.Equals(key, prevKey, StringComparison.Ordinal))
            {
                labels.Add(FinisherLabel(executed[i]));
                prevKey = key;
            }
            groupOfNode.Add(labels.Count - 1);
        }

        if (totalNodeCount > executed.Count)
        {
            labels.Add(null); // trailing rune/item bonus hits — not an authored cast slot
            for (int i = executed.Count; i < totalNodeCount; i++) groupOfNode.Add(labels.Count - 1);
        }

        return (groupOfNode, labels);
    }

    /// <summary>
    /// (M20 §6.2) Turns <see cref="DamageEngine.PlacementSearchResult"/> into the four display
    /// fields <see cref="ComboResult"/> carries. The sequence string lists AUTHORED slot labels
    /// only (the terminal bonus-hit group has no label and is omitted), with the variance slot
    /// shown at its optimal position — a placement past the terminal group therefore renders as
    /// "last", which is what it means for a player anyway.
    /// </summary>
    private static (BurstCeilingStatus Status, double Damage, string? NodeLabel, string? Sequence) DescribePlacement(
        DamageEngine.PlacementSearchResult placement, List<string?> groupLabels, double authoredTotal)
    {
        var status = placement.Status switch
        {
            DamageEngine.PlacementStatus.MultipleVarianceGroups => BurstCeilingStatus.MultipleVarianceNodes,
            DamageEngine.PlacementStatus.VarianceUnnecessary => BurstCeilingStatus.VarianceUnnecessary,
            DamageEngine.PlacementStatus.Optimized => placement.OptimalGroupIndex == placement.AuthoredGroupIndex
                ? BurstCeilingStatus.AlreadyOptimal
                : BurstCeilingStatus.Optimized,
            _ => BurstCeilingStatus.None,
        };

        if (status == BurstCeilingStatus.None) return (status, 0, null, null);

        string? nodeLabel = placement.AuthoredGroupIndex >= 0 && placement.AuthoredGroupIndex < groupLabels.Count
            ? groupLabels[placement.AuthoredGroupIndex]
            : null;

        if (status == BurstCeilingStatus.MultipleVarianceNodes)
            return (status, Math.Round(authoredTotal, 2), nodeLabel, null);

        // Authored labels with the variance slot re-inserted at its optimal position.
        var others = new List<string?>();
        for (int g = 0; g < groupLabels.Count; g++)
            if (g != placement.AuthoredGroupIndex && groupLabels[g] is { } l) others.Add(l);
        int insertAt = Math.Min(Math.Max(placement.OptimalGroupIndex, 0), others.Count);
        if (nodeLabel is not null) others.Insert(insertAt, nodeLabel);

        return (status, Math.Round(placement.MaxTotalDamage, 2), nodeLabel, string.Join(' ', others));
    }

    /// <summary>
    /// Auto-Trigger Rune #1/6 — Axiom Arcanist (id 8224): 1.12x (single-target) or 1.08x (AoE)
    /// multiplier on damage/healing/shielding for EVERY node that is the champion's own Ultimate
    /// (an executed node is the Ultimate iff <c>Id.StartsWith("R_")</c> — see
    /// ComboSettingsView.xaml.cs's node-id assignment, the only reliable slot signal available;
    /// M03's <see cref="ComboNode"/> has no separate slot field). Runs on the raw M03
    /// <paramref name="executed"/> list, BEFORE <see cref="BuildDamageNode"/> folds
    /// RatioAD/RatioBonusAD into one flat number, so all four of the node's own flat/ratio
    /// damage-bearing fields scale together and the multiplier correctly propagates through
    /// mitigation/crit downstream (uniformly larger inputs to an otherwise-unchanged pipeline). A
    /// combo repeating the ultimate (e.g. a champion with a recastable R) has each "R_" node scaled
    /// independently.
    ///
    /// (GOLDEN #3 Garen R fix) <see cref="ComboNode.ExecutePercent"/> is ALSO scaled — a %current/
    /// %missing-HP ultimate hit (<see cref="ComboExecuteType.CurrentHp"/>/<see cref="ComboExecuteType.MissingHp"/>,
    /// e.g. Garen R's "+25% of target's missing health" term) carries its ENTIRE contribution
    /// through this coefficient, not through Damage/RatioAD/RatioAP (which stay 0 for that hit) — so
    /// the pre-existing four-field scaling silently never amplified it, an under-count invisible
    /// until a champion combining an auto-trigger-rune-eligible Ultimate with a %HP execute hit was
    /// golden-verified (docs/reports/golden/GOLDEN_03_GAREN.md §5: measured R = 1.12×(150+0.25×missing),
    /// both terms amplified together, not just the flat one). <see cref="ComboExecuteType.Threshold"/>
    /// nodes use <see cref="ComboNode.ExecuteThreshold"/> (a flat HP cutoff, never
    /// <see cref="ComboNode.ExecutePercent"/>) for their condition, so this scaling is a no-op for
    /// them (ExecutePercent is unused/0 on a Threshold node) — never touches the threshold itself.
    ///
    /// AoE-vs-single-target (M21 "AoeUltimate" tag, added loop 46 specifically to close this
    /// approximation): <see cref="SkillDamageDb.GetSlotTags(string, string)"/> for the champion's
    /// "R" slot is consulted once per call. Present → 1.08 (matches the real rune's "AoE damage is
    /// reduced to a 8% increase" text). Absent (including any not-yet-curated champion, which
    /// returns an empty tag list, never throws) → 1.12, the single-target default — so an
    /// uncurated champion degrades to the OLD pre-loop-46 behavior rather than a wrong 1.08, never
    /// a fabricated classification (M21 §5's "skip if not confidently assignable" rule, honored
    /// here as "no tag = single-target default" rather than guessing).
    /// </summary>
    private static void ApplyAxiomArcanistUltimateMultiplier(List<ComboNode> executed, ExecutionContext context)
    {
        if (!context.UserRuneConfig.SelectedRuneIds.Contains(AxiomArcanistRuneId)) return;

        bool isAoe = SkillDamageDb.GetSlotTags(context.ChampionId, "R").Contains("AoeUltimate");
        double multiplier = isAoe ? AxiomArcanistAoeMultiplier : AxiomArcanistSingleTargetMultiplier;

        for (int i = 0; i < executed.Count; i++)
        {
            var n = executed[i];
            if (!n.Id.StartsWith("R_", StringComparison.Ordinal)) continue;
            executed[i] = n with
            {
                Damage = n.Damage * multiplier,
                RatioAD = n.RatioAD * multiplier,
                RatioBonusAD = n.RatioBonusAD * multiplier,
                RatioAP = n.RatioAP * multiplier,
                ExecutePercent = n.ExecutePercent * multiplier,
            };
        }
    }

    /// <summary>
    /// Manual, node-attached active-item effects — <see cref="ComboNode.AttachedItemId"/>, distinct
    /// from <see cref="ComboRunner"/>'s build-list item-proc pipeline (on-hit/spellblade/stack-then-
    /// consume, auto-applied across the WHOLE build) and from the auto-trigger rune engine above.
    /// Only an item <see cref="ItemEffectDb.Get"/> classifies as
    /// <see cref="ItemTrigger.ManualActiveBurst"/> (currently just Deathfire Grasp, 3128) is
    /// read here — every other <see cref="ComboNode.AttachedItemId"/> value (an uncovered item id,
    /// or a covered item with a different trigger) is left exactly as the loop-38 LEAD DECISION
    /// specified: a decorative sub-icon with zero damage effect.
    ///
    /// Two effects per covered attached node, mirroring the item's real "Active - The Silence" text:
    /// (1) an instant burst — <see cref="ItemEffect.TargetMaxHpPercent"/> of
    /// <see cref="ExecutionContext.Defender"/>'s Max HP, returned as a new trailing damage node (see
    /// below for why it isn't folded into the attached node's own <c>Damage</c> field) — still
    /// mitigated/crit-tracked exactly like any other node once appended to <c>damageNodes</c>, no
    /// special-casing needed downstream; (2) a damage-amplification WINDOW —
    /// every OTHER node whose own <paramref name="elapsedAtNode"/> timestamp falls strictly after
    /// the attached node's and within <see cref="ItemEffect.AmplifyDurationSeconds"/> of it
    /// has its damage multiplied by <c>1 + AmplifyDamagePercent</c>. This is intentionally source-
    /// agnostic (it scales whatever damage that later node already carries, whether it's a curated
    /// ability hit, an on-hit item proc, or an already-folded burn/DoT total) since the real item
    /// debuff amplifies ALL damage the target takes, not just this item's own burst. The attached
    /// node itself is excluded from its own window (strict "after", matching the real game's "this
    /// hit lands, THEN the debuff starts").
    ///
    /// The burst is returned as a NEW trailing <see cref="Damage.ComboNode"/> (its own
    /// <see cref="ItemEffect.DamageType"/> — Magic for Deathfire Grasp — carried independently),
    /// rather than folded into the attached node's own <c>Damage</c> field: a node's
    /// <see cref="Damage.ComboNode"/> shape has exactly ONE <see cref="Damage.DamageType"/> for its
    /// whole damage number, so merging a Magic burst into a node whose own skill/AA damage is
    /// Physical (or vice versa) would silently mis-mitigate one of the two halves — mirrors why the
    /// 6 auto-trigger runes above append trailing bonus nodes instead of folding into whatever node
    /// they're near.
    ///
    /// Multiple attached instances (an edge case the combo editor doesn't prevent, even though a
    /// real player can only own one Deathfire Grasp) have their windows stack MULTIPLICATIVELY on
    /// any node covered by more than one — the same convention Riot typically uses for stacked
    /// "increased damage taken" effects, kept simple rather than guessing at a real interaction the
    /// game would never actually let happen. The 90s real cooldown is not modeled (single-
    /// application-per-attached-node, consistent with this project's existing spellblade/manual-rune
    /// "at most once per combo" precedent). elapsedAtNode is the same running
    /// <see cref="ComboNode.CastTime"/>+<see cref="ComboNode.Delay"/> accumulation <see cref="Execute"/>
    /// already computes for <see cref="ComboResult.TotalCastTime"/>, snapshotted per node — an
    /// approximation (ignores <see cref="ComboNode.TravelTime"/>/real cast-animation timing)
    /// consistent with this project's existing linear-combo-timeline conventions elsewhere.
    /// </summary>
    private static List<Damage.ComboNode> ApplyAttachedItemEffects(
        IReadOnlyList<ComboNode> executed, IReadOnlyList<double> elapsedAtNode,
        List<Damage.ComboNode> damageNodes, ExecutionContext context)
    {
        var burstNodes = new List<Damage.ComboNode>();
        var windows = new List<(double Start, double End, double Multiplier)>();

        for (int i = 0; i < executed.Count; i++)
        {
            string? itemId = executed[i].AttachedItemId;
            if (string.IsNullOrEmpty(itemId)) continue;

            var effect = ItemEffectDb.Get(itemId);
            if (effect is null || effect.Trigger != ItemTrigger.ManualActiveBurst) continue;

            if (effect.TargetMaxHpPercent is > 0)
            {
                double burst = effect.TargetMaxHpPercent.Value * context.Defender.MaxHP;
                burstNodes.Add(new Damage.ComboNode(
                    NodeId: $"{executed[i].Id}#item{itemId}",
                    Damage: burst,
                    RatioAD: 0,
                    RatioAP: 0,
                    DamageType: MapItemDamageType(effect.DamageType)));
            }

            if (effect.AmplifyDamagePercent is > 0 && effect.AmplifyDurationSeconds is > 0)
            {
                windows.Add((elapsedAtNode[i], elapsedAtNode[i] + effect.AmplifyDurationSeconds.Value,
                    1.0 + effect.AmplifyDamagePercent.Value));
            }
        }

        if (windows.Count > 0)
        {
            for (int j = 0; j < executed.Count; j++)
            {
                double multiplier = 1.0;
                foreach (var window in windows)
                {
                    if (elapsedAtNode[j] > window.Start && elapsedAtNode[j] <= window.End)
                        multiplier *= window.Multiplier;
                }
                if (multiplier == 1.0) continue;

                var n = damageNodes[j];
                damageNodes[j] = n with
                {
                    Damage = n.Damage * multiplier,
                    RatioAD = n.RatioAD * multiplier,
                    RatioAP = n.RatioAP * multiplier,
                };
            }
        }

        return burstNodes;
    }

    /// <summary>
    /// Part 3 (loop 50): per-node FORCE-APPLIED runes — <see cref="ComboNode.AttachedRuneId"/>. The
    /// theorycraft sibling of <see cref="ApplyAttachedItemEffects"/>: an attached rune is applied to
    /// its node UNCONDITIONALLY (whether or not equipped, whether or not its real trigger condition
    /// is met), because the user explicitly asked "compute as if this rune fired here." This is the
    /// deliberate exception to the rest of the rune pipeline's equipped-and-condition-gated rule.
    ///
    /// Two shapes, mirroring how the auto-trigger engine already treats these ids:
    /// (1) Axiom Arcanist (8224) is a pure damage MULTIPLIER (no additive <see cref="RuneEffectDb"/>
    ///     formula) — it scales the ATTACHED node's own damage in place (Damage/RatioAD/RatioAP) by
    ///     the ultimate multiplier (AoE-vs-single resolved from the champion's "R" tag, same rule as
    ///     <see cref="ApplyAxiomArcanistUltimateMultiplier"/>). Applied to whatever node the user
    ///     attached it to — the explicit force-apply intent — not restricted to an "R_" node.
    /// (2) Every other rune with a <see cref="RuneEffectDb.Get"/> formula (the manual runes +
    ///     Summon Aery/Electrocute/Hail of Blades/Dark Harvest/Conqueror) becomes ONE new trailing
    ///     bonus node (its evaluated damage/type) — the same additive-hit shape the manual-rune and
    ///     auto-rune paths use. An attached id with neither (no formula and not Axiom, e.g. First
    ///     Strike 8369 whose real effect is an amplifier, not an additive source) is a decorative
    ///     no-op, exactly as an uncovered <see cref="ComboNode.AttachedItemId"/> is.
    ///
    /// damageNodes[0..executed.Count) stays index-aligned with executed (Axiom mutates in place at
    /// that index; additive runes only append), so the suffix-threshold loop's 1:1 assumption holds.
    /// NOTE: if a user attaches a rune they ALSO have equipped, its auto-path contribution and this
    /// force-apply contribution both count — intended (the attach is an explicit "also apply here").
    /// </summary>
    private static List<Damage.ComboNode> ApplyAttachedRuneEffects(
        IReadOnlyList<ComboNode> executed, List<Damage.ComboNode> damageNodes,
        ExecutionContext context, RuneCasterStats casterStats)
    {
        var bonusNodes = new List<Damage.ComboNode>();

        for (int i = 0; i < executed.Count; i++)
        {
            string? runeId = executed[i].AttachedRuneId;
            if (string.IsNullOrEmpty(runeId)) continue;

            if (runeId == AxiomArcanistRuneId)
            {
                bool isAoe = SkillDamageDb.GetSlotTags(context.ChampionId, "R").Contains("AoeUltimate");
                double multiplier = isAoe ? AxiomArcanistAoeMultiplier : AxiomArcanistSingleTargetMultiplier;
                var n = damageNodes[i];
                damageNodes[i] = n with
                {
                    Damage = n.Damage * multiplier,
                    RatioAD = n.RatioAD * multiplier,
                    RatioAP = n.RatioAP * multiplier,
                };
                continue;
            }

            if (RuneEffectDb.Get(runeId) is { } formula)
            {
                var (bonus, type) = RuneEffectDb.Evaluate(formula, casterStats);
                if (bonus > 0)
                    bonusNodes.Add(new Damage.ComboNode(
                        NodeId: $"{executed[i].Id}#rune{runeId}",
                        Damage: bonus,
                        RatioAD: 0,
                        RatioAP: 0,
                        DamageType: MapRuneDamageType(type)));
            }
        }

        return bonusNodes;
    }

    /// <summary>Maps <see cref="HitDamageType"/> (item_effects.json's curated damage type) to
    /// <see cref="Damage.DamageType"/> (the M05 engine's own type) — same 3-member shape as
    /// <see cref="MapRuneDamageType"/> below, kept as its own explicit switch (rather than an enum
    /// cast) so a future divergence between the two enums fails loudly instead of silently
    /// mismapping.</summary>
    private static Damage.DamageType MapItemDamageType(HitDamageType type) => type switch
    {
        HitDamageType.Physical => Damage.DamageType.Physical,
        HitDamageType.Magic => Damage.DamageType.Magic,
        HitDamageType.True => Damage.DamageType.True,
        _ => Damage.DamageType.Physical,
    };

    /// <summary>
    /// Auto-Trigger Runes #2-6/6 — Summon Aery, Electrocute, Hail of Blades, Dark Harvest,
    /// Conqueror (ids 8214/8112/9923/8128/8010). Each is applied ONLY when both (a) the user
    /// selected it (<see cref="UserRuneConfig.SelectedRuneIds"/>) AND (b) the executed node
    /// sequence genuinely satisfies its real trigger condition — see rune_effects.json's per-id
    /// "_note" for the exact wiki citation/trigger and
    /// docs/reports/agent/auto_rune_trigger_engine_report.md for worked numeric examples. Level/
    /// AD/AP-ratio/adaptive-type math is entirely reused from <see cref="RuneEffectDb.Evaluate"/>
    /// (no second parallel formula evaluator).
    ///
    /// Returns synthetic trailing bonus nodes (appended to <paramref name="damageNodes"/> by the
    /// caller, same trailing-node shape as the pre-existing manual-rune <c>runeBonusNodes</c>
    /// pattern) for Summon Aery/Electrocute/Hail of Blades/Dark Harvest, and Conqueror's resolved
    /// heal percent (non-null only when its full-stack condition was genuinely met) for the
    /// caller to apply once the FINAL <see cref="DamageCalcResult"/> is known (the heal scales
    /// off the combo's total damage, which must include this method's own bonuses first).
    ///
    /// Conqueror's own per-node ramping bonus is the one exception to "trailing synthetic node":
    /// it mutates <paramref name="damageNodes"/> IN PLACE at the SAME index as the triggering
    /// node, because that extra damage is genuinely "this node's own AD/AP ratio, applied to more
    /// AD/AP" (not an independently-typed proc) — folding it into the node's own Damage field
    /// keeps it under that node's own (already-correct) damage type/mitigation. Electrocute and
    /// Dark Harvest, by contrast, use the trailing-synthetic-node form even though the spec
    /// offered "fold into the triggering node's own Damage field" as an alternative: their bonus
    /// is genuinely adaptive (independently resolved to ONE specific physical/magic type via
    /// <see cref="RuneEffectDb.Evaluate"/>'s <see cref="AdaptiveRule.ByRatioContribution"/>), so
    /// folding it into an arbitrary triggering node's own (possibly different) DamageType would
    /// silently mis-mitigate part of the bonus — a genuine correctness risk, not just a style
    /// choice, so the trailing-node form was chosen for both (documented deviation, see Agent
    /// Report Notes for Reviewer).
    /// </summary>
    private (List<Damage.ComboNode> BonusNodes, double? ConquerorHealPercent) ApplyRemainingAutoTriggeredRunes(
        List<ComboNode> executed, List<Damage.ComboNode> damageNodes, ExecutionContext context, RuneCasterStats casterStats)
    {
        var selected = context.UserRuneConfig.SelectedRuneIds;
        var bonusNodes = new List<Damage.ComboNode>();

        var damagingIndices = new List<int>();
        for (int i = 0; i < executed.Count; i++)
            if (IsDamagingNode(executed[i])) damagingIndices.Add(i);

        // --- Summon Aery (8214): fires once per combo (return-travel cooldown), adaptive.
        if (selected.Contains(SummonAeryRuneId) && damagingIndices.Count >= 1
            && RuneEffectDb.Get(SummonAeryRuneId) is { } aeryFormula)
        {
            var (bonus, type) = RuneEffectDb.Evaluate(aeryFormula, casterStats);
            bonusNodes.Add(new Damage.ComboNode(
                NodeId: $"autorune#{SummonAeryRuneId}", Damage: bonus, RatioAD: 0, RatioAP: 0,
                DamageType: MapRuneDamageType(type)));
        }

        // --- Electrocute (8112): needs >= 3 separate damaging hits within the combo.
        if (selected.Contains(ElectrocuteRuneId) && damagingIndices.Count >= 3
            && RuneEffectDb.Get(ElectrocuteRuneId) is { } electrocuteFormula)
        {
            var (bonus, type) = RuneEffectDb.Evaluate(electrocuteFormula, casterStats);
            bonusNodes.Add(new Damage.ComboNode(
                NodeId: $"autorune#{ElectrocuteRuneId}", Damage: bonus, RatioAD: 0, RatioAP: 0,
                DamageType: MapRuneDamageType(type)));
        }

        // --- Hail of Blades (9923): true damage on each of the first 3 AA nodes (or fewer).
        if (selected.Contains(HailOfBladesRuneId) && RuneEffectDb.Get(HailOfBladesRuneId) is { } hobFormula)
        {
            var (bonus, type) = RuneEffectDb.Evaluate(hobFormula, casterStats);
            int aaHit = 0;
            foreach (var n in executed)
            {
                if (aaHit >= 3) break;
                if (n.NodeType != ComboNodeType.Aa) continue;
                aaHit++;
                bonusNodes.Add(new Damage.ComboNode(
                    NodeId: $"autorune#{HailOfBladesRuneId}_{aaHit}", Damage: bonus, RatioAD: 0, RatioAP: 0,
                    DamageType: MapRuneDamageType(type))); // formula's damageType is fixed "True", never adaptive
            }
        }

        // --- Dark Harvest (8128): only if the target genuinely crosses below 50% HP mid-combo.
        // Preliminary probe uses damageNodes exactly as built so far (Axiom Arcanist's ultimate
        // scaling already baked in structurally, but none of this method's own bonuses yet).
        if (selected.Contains(DarkHarvestRuneId) && RuneEffectDb.Get(DarkHarvestRuneId) is { } dhFormula)
        {
            var probe = _damageEngine.Calculate(new DamageCalcInput(damageNodes, context.Attacker, context.Defender));
            double remainingHp = context.Defender.CurrentHP;
            bool triggered = false;
            foreach (var bd in probe.Breakdown)
            {
                remainingHp -= bd.Damage;
                if (remainingHp <= context.Defender.MaxHP * 0.5) { triggered = true; break; }
            }
            if (triggered)
            {
                var (bonus, type) = RuneEffectDb.Evaluate(dhFormula, casterStats);
                bonusNodes.Add(new Damage.ComboNode(
                    NodeId: $"autorune#{DarkHarvestRuneId}", Damage: bonus, RatioAD: 0, RatioAP: 0,
                    DamageType: MapRuneDamageType(type)));
            }
        }

        // --- Conqueror (8010): per-node ramping AD/AP bonus + full-stack heal condition.
        double? conquerorHealPercent = null;
        if (selected.Contains(ConquerorRuneId) && RuneEffectDb.Get(ConquerorRuneId) is { } conquerorFormula)
        {
            int stacksPerHit = context.CasterIsMelee ? 2 : 1;
            const int maxStacks = 12;
            // RuneEffectDb.Evaluate on this entry (bonusAdRatio/apRatio/health% all 0) returns
            // exactly the level-linear Adaptive-Force-per-stack value (1.8-4) as "bonus", and the
            // caster's AD-vs-AP affinity (rune_effects.json "8010"'s adaptive=ByBonusAdVsAp) as
            // "type" — resolved ONCE here for the whole combo.
            var (forcePerStack, resolvedType) = RuneEffectDb.Evaluate(conquerorFormula, casterStats);
            bool isAdaptiveAd = resolvedType == RuneDamageType.PHYSICAL;

            for (int k = 0; k < damagingIndices.Count; k++)
            {
                // Stacks already accumulated GOING INTO this hit (from the prior k hits only —
                // this hit's own damage does not buff itself, matching real Conqueror timing).
                double stacksSoFar = Math.Min(maxStacks, k * stacksPerHit);
                if (stacksSoFar <= 0) continue;
                double adaptiveForce = stacksSoFar * forcePerStack;
                int idx = damagingIndices[k];
                var n = executed[idx];
                double nodeBonus = adaptiveForce * (isAdaptiveAd ? (n.RatioAD + n.RatioBonusAD) : n.RatioAP);
                if (nodeBonus <= 0) continue;
                damageNodes[idx] = damageNodes[idx] with { Damage = damageNodes[idx].Damage + nodeBonus };
            }

            int neededHits = context.CasterIsMelee ? 6 : 12; // stacksPerHit*neededHits == maxStacks
            if (damagingIndices.Count >= neededHits)
                conquerorHealPercent = context.CasterIsMelee ? ConquerorMeleeHealPercent : ConquerorRangedHealPercent;
        }

        return (bonusNodes, conquerorHealPercent);
    }

    public string Serialize(ComboGraph graph) => JsonSerializer.Serialize(graph, JsonOptions);

    public ComboGraph Deserialize(string json)
        => JsonSerializer.Deserialize<ComboGraph>(json, JsonOptions)
            ?? throw new FormatException("ComboGraph JSON deserialized to null");

    /// <summary>
    /// Condition evaluation (spec Data Model's <see cref="Condition"/> + Agent
    /// Implementation Notes). See M03 report Notes for Reviewer for the full
    /// per-branch reasoning:
    /// - STACK_GTE: compares the node's own declared <see cref="ComboNode.Stack"/>
    ///   against <see cref="Condition.Value"/>; if the node names a
    ///   <see cref="ComboNode.SpecialPropertyKey"/>, that raw stack count is first run
    ///   through <see cref="ChampionRepository.GetSpecialProperty"/>'s formula (e.g. a
    ///   level-scaled passive stack value) instead of being used literally — this is
    ///   the generic, non-champion-branching hook the Agent Implementation Notes ask for.
    /// - HP_BELOW: <see cref="Condition.Value"/> is read as a 0..1 fraction of
    ///   <c>defender.MaxHP</c> (consistent with M05's own <c>ExecutePercent</c> convention).
    /// - MANA_GTE: gates against the combo's own running mana accumulator (nodes
    ///   executed so far), not an external mana pool — <c>ExecutionContext.attacker</c>
    ///   is M05's <see cref="AttackerStat"/>, which has no mana field at all, so there is
    ///   no live mana-pool value this module is allowed to check against without
    ///   synthesizing one (forbidden by the same "don't synthesize stats" boundary M05
    ///   already established for defender stats).
    /// </summary>
    private static bool EvaluateCondition(ComboNode node, ExecutionContext context, double manaAccumulatedSoFar)
    {
        if (node.Condition is null) return true;
        var condition = node.Condition;

        switch (condition.Type)
        {
            case ConditionType.StackGte:
                double stack = node.Stack ?? 0;
                if (node.SpecialPropertyKey is not null)
                {
                    var special = ChampionRepository.GetSpecialProperty(context.ChampionId, node.SpecialPropertyKey);
                    if (special is not null)
                    {
                        var vars = new Dictionary<string, double>
                        {
                            ["stack"] = stack,
                            ["level"] = context.Attacker.Level,
                        };
                        stack = FormulaParser.Evaluate(special.ValueFormula, vars);
                    }
                }
                return stack >= condition.Value;

            case ConditionType.HpBelow:
                return context.Defender.CurrentHP <= condition.Value * context.Defender.MaxHP;

            case ConditionType.ManaGte:
                return manaAccumulatedSoFar >= condition.Value;

            default:
                return true;
        }
    }

    /// <summary>
    /// Translates one architecture-defined <see cref="ComboNode"/> into M05's
    /// already-built, already-passed <see cref="Damage.ComboNode"/> shape. Two
    /// deliberate reconciliations happen here (see M03 report Notes for Reviewer):
    ///
    /// 1. Architecture's node has separate <c>ratioAD</c>/<c>ratioBonusAD</c>, but M05's
    ///    <see cref="Damage.ComboNode"/> only has one combined <c>RatioAD</c> (applied to
    ///    <c>attacker.Ad + attacker.BonusAD</c>). Rather than touching M05's
    ///    already-reviewed contract, the AD contribution is folded here by hand
    ///    (<c>ratioAD*attacker.Ad + ratioBonusAD*attacker.BonusAD</c>) and added into the
    ///    flat <see cref="Damage.ComboNode.Damage"/> field, with
    ///    <see cref="Damage.ComboNode.RatioAD"/> passed as 0 so M05 doesn't double-apply it.
    ///    <see cref="Damage.ComboNode.RatioAP"/> is passed straight through, unmodified.
    /// 2. Architecture's node has no <c>executePercent</c> field, but M05's
    ///    <see cref="Damage.ComboNode.ExecutePercent"/> is required for the
    ///    <c>CURRENT_HP</c>/<c>MISSING_HP</c> execute types. Resolved by adding an
    ///    <see cref="ComboNode.ExecutePercent"/> field to this module's own node type
    ///    (the same kind of "add the minimum field the pipeline needs" move M05 itself
    ///    made for the identical gap) and passing it straight through.
    /// </summary>
    private static Damage.ComboNode BuildDamageNode(ComboNode node, ExecutionContext context)
    {
        double foldedDamage = node.Damage
            + node.RatioAD * context.Attacker.Ad
            + node.RatioBonusAD * context.Attacker.BonusAD;

        return new Damage.ComboNode(
            NodeId: node.Id,
            Damage: foldedDamage,
            RatioAD: 0,
            RatioAP: node.RatioAP,
            DamageType: MapDamageType(node.DamageType),
            // (M27) ORed with the curated per-hit flag so a non-AA ability (e.g. Garen E) can also
            // crit; every pre-existing node has CanCrit=false, so this is additive-only.
            CanCrit: node.NodeType == ComboNodeType.Aa || node.CanCrit,
            ExecuteType: MapExecuteType(node.ExecuteType),
            ExecuteThreshold: node.ExecuteThreshold ?? 0,
            ExecutePercent: node.ExecutePercent,
            CritDamageScalar: node.CritDamageScalar,
            HpBelowGateFraction: node.HpBelowGate); // (§27 (B)) carry the dynamic low-HP on-hit gate
    }

    private static Damage.DamageType MapDamageType(ComboDamageType type) => type switch
    {
        ComboDamageType.Physical => Damage.DamageType.Physical,
        ComboDamageType.Magic => Damage.DamageType.Magic,
        ComboDamageType.True => Damage.DamageType.True,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Maps M06's own <see cref="RuneDamageType"/> (distinct from <see cref="Damage.DamageType"/>
    /// for the same reason <see cref="ComboDamageType"/> is — see RuneEngine.cs's enum doc comment)
    /// into M05's mitigation-category enum, for a rune bonus node's damage type.</summary>
    private static Damage.DamageType MapRuneDamageType(RuneDamageType type) => type switch
    {
        RuneDamageType.PHYSICAL => Damage.DamageType.Physical,
        RuneDamageType.MAGIC => Damage.DamageType.Magic,
        RuneDamageType.TRUE => Damage.DamageType.True,
        _ => Damage.DamageType.Magic,
    };

    private static Damage.ExecuteType MapExecuteType(ComboExecuteType type) => type switch
    {
        ComboExecuteType.None => Damage.ExecuteType.None,
        ComboExecuteType.Threshold => Damage.ExecuteType.Threshold,
        ComboExecuteType.CurrentHp => Damage.ExecuteType.CurrentHp,
        ComboExecuteType.MissingHp => Damage.ExecuteType.MissingHp,
        ComboExecuteType.BaseWithMissingHpBonus => Damage.ExecuteType.BaseWithMissingHpBonus,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Display label for the suffix-threshold "finisher" node (M20 §6 item 3) on the
    /// combo card: the ability slot (Q/W/E/R) or "AA" for an auto-attack — same convention as
    /// <see cref="ComboRunner"/>'s own command-string labeling (editor node ids are "{slot}_{n}",
    /// e.g. "R_0", to allow a skill to repeat in one combo).</summary>
    private static string FinisherLabel(ComboNode node)
        => node.NodeType == ComboNodeType.Aa ? "AA" : SlotOf(node.Id).ToUpperInvariant();

    /// <summary>The bare skill slot from a combo-node id — small local copy of
    /// <see cref="ComboRunner"/>'s identical private helper (not shared across files to avoid
    /// widening either type's visibility for a two-line helper).</summary>
    private static string SlotOf(string id)
    {
        int u = id.IndexOf('_');
        return u < 0 ? id : id[..u];
    }
}

/// <summary>NodeType per 01_SYSTEM_ARCHITECTURE.md 4.1 Combo Node Graph.</summary>
public enum ComboNodeType { Skill, Aa, Passive, Rune, Item, Execute, Summoner }

/// <summary>DamageType per 01_SYSTEM_ARCHITECTURE.md 4.1 (this module's own copy of the
/// node's declared damage type — distinct from <see cref="Damage.DamageType"/>, which
/// <see cref="ComboEngine"/> maps into when building an M05 node).</summary>
public enum ComboDamageType { Physical, Magic, True }

/// <summary>ExecuteType per 01_SYSTEM_ARCHITECTURE.md 4.1 (mapped into
/// <see cref="Damage.ExecuteType"/> when building an M05 node), plus
/// <see cref="BaseWithMissingHpBonus"/> for Kraken Slayer's real missing-health-scaling
/// item proc (see <see cref="Damage.ExecuteType.BaseWithMissingHpBonus"/>).</summary>
public enum ComboExecuteType { None, Threshold, CurrentHp, MissingHp, BaseWithMissingHpBonus }

/// <summary>Condition type per M03 spec's own Data Model addition. <see cref="EveryNth"/> (M23
/// Phase 2: Value = the 1-indexed ordinal that fires, e.g. Kraken Slayer's StacksRequired+1) and
/// <see cref="OnHitEmpowered"/> (Value unused: fires on the first on-hit trigger after an
/// ability cast — spellblade) are the two additions <c>BonusEffect</c> needed that didn't already
/// exist on this enum; every other M23 archetype condition reuses <see cref="StackGte"/>/
/// <see cref="HpBelow"/>/<see cref="ManaGte"/> or the implicit "Always" (no <see cref="Condition"/>
/// set at all).</summary>
public enum ConditionType
{
    StackGte, HpBelow, ManaGte, EveryNth, OnHitEmpowered,

    // ── (M25 §11.G) conditional-bonus conditions (docs/modules/M25_CONDITIONAL_VARIFORM_CATALOG.md) ──
    // A curated bonus (a separate hit or an amplified variant) that applies only when the condition
    // holds. Distinct from an EXECUTE (kill-line): these INCREASE damage, they never insta-kill.
    // Each is classified AutoResolvable vs UserAssumed by <see cref="ConditionResolution"/>.

    /// <summary>Target has no nearby allied champions (Kha'Zix Q, Mordekaiser Q). Enemy positional
    /// state — not exposed by the Live Client API, so UserAssumed.</summary>
    VsIsolated,

    /// <summary>Target carries a specific debuff (Cassiopeia E vs her poison, Varus blight-detonate,
    /// LeBlanc sigil). Enemy debuff state — not exposed, UserAssumed. (A future refinement may
    /// auto-resolve the subset whose debuff SOURCE is the caster's own earlier combo cast.)</summary>
    VsDebuffed,

    /// <summary>Cast from melee range (Talon Q empowered/crit). Caster positioning at cast time —
    /// not tracked, UserAssumed.</summary>
    MeleeRangeCast,

    /// <summary>Caster's own resource (Renekton fury, Rengar ferocity, Rumble heat) &gt;= Value.
    /// The active player's current resource IS live (GameSnapshot ResourceValue) -> AutoResolvable.</summary>
    ResourceGte,

    /// <summary>(M28 — docs/modules/M28_NODE_OPTION_UX.md §1 "binary conditional hit") The ability
    /// physically connects with a wall/terrain feature (K'Sante R "Demacian Justice" slam,
    /// generically any 벽꿍-style bonus). Not a target/caster STATE — a positional/terrain fact the
    /// Live Client API has no concept of at all, so UserAssumed (default unmet — the M28 checkbox's
    /// OFF/floor state). Distinct from <see cref="MeleeRangeCast"/> (caster's own cast range) and
    /// <see cref="VsIsolated"/>/<see cref="VsDebuffed"/> (target state) — this is terrain.</summary>
    HitsWall,

    /// <summary>(§76) The cast lands in/from BRUSH (Maokai E's brush-enhanced sapling). Like
    /// <see cref="HitsWall"/> this is terrain, not a target or caster STATE, and the Live Client API
    /// exposes no positional data at all — so UserAssumed, default unmet (the ordinary sapling is the
    /// P2 floor and the enhanced value is the ceiling).</summary>
    InBrush,

    /// <summary>(loop 479) This cast is the EMPOWERED form of the ability — Viktor's Aftershock
    /// evolution, Syndra's Transcendent W, Udyr's Awakened claw, Jhin's fourth shot. What they share
    /// is that the empowerment is a fact about the champion or the shot, not a number on any bar:
    /// unlike <see cref="ResourceGte"/> there is nothing to read and compare against a threshold.
    ///
    /// <para>Some of these are permanent once chosen (an evolution), some last a window (an Awakened
    /// stance), and one is a position in an ammo cycle (the fourth shot). They are one condition
    /// because the engine treats them identically: the Live Client API reports none of them, so the
    /// user asserts it. UserAssumed, default unmet — the floor is the ordinary cast and ticking the
    /// box raises it, with the range spanning both. That is also what makes it useful while
    /// PLANNING: a combo written before the upgrade exists can still show what it becomes.</para>
    /// </summary>
    Upgraded,

    /// <summary>(loop 483) The cast landed on its SWEET SPOT — the narrow part of the shape that is
    /// worth more than the rest. Aatrox Q's outer edge (x1.75), Xerath W's epicentre (x1.667) and
    /// Lillia W's inner circle (x3) are all the same idea and all carry an explicit BIN multiplier
    /// beside the ordinary calc.
    ///
    /// <para>Positional, so UserAssumed and default unmet, exactly like <see cref="HitsWall"/>: the
    /// Live Client API exposes no cast geometry, and the ordinary hit is the honest floor. What makes
    /// the toggle worth having is that landing the sweet spot is a SKILL the player either has or is
    /// practising — the range shows both halves of the ability, so a combo can be planned around
    /// hitting it and compared against missing it.</para></summary>
    SweetSpot,

    /// <summary>(loop 485) The attack or cast landed FROM BEHIND the target. Shaco's Backstab is the
    /// case: Two-Shiv Poison thrown from behind carries an extra dagger payload that a dagger thrown
    /// from the front does not.
    ///
    /// <para>Positional like <see cref="SweetSpot"/> and kept separate from it because it is a
    /// different fact — a sweet spot is about where the SHAPE landed on the target, this is about
    /// where the CASTER was. Neither is in the Live Client API, so both are UserAssumed and default
    /// unmet; the front-facing number stays the floor.</para></summary>
    FromBehind,
}

/// <summary>M03 spec's own <c>Condition</c> Data Model addition.</summary>
public sealed record Condition(ConditionType Type, double Value);

/// <summary>
/// (M25 §11.G) Classifies whether a <see cref="ConditionType"/> can be resolved from the active
/// player's own live snapshot (AutoResolvable) or depends on enemy/positional state the Live Client
/// API does not expose (UserAssumed). Per Hard Rule P2 (no inference beyond public info), a
/// UserAssumed condition must NOT be silently assumed true — it surfaces as a user knob and defaults
/// to UNMET (the conservative RangeMin end), while its met state widens RangeMax. AutoResolvable
/// conditions read live state directly (own resource / own HP).
///
/// Live availability (verified, M24 Appendix C + GameSnapshot): the active player's own resource
/// (ResourceValue) and HP (CurrentHealth/MaxHealth) are exposed; ENEMY current HP, stack counts,
/// isolation, and debuffs are NOT, and neither is cast-range/position.
/// </summary>
public static class ConditionResolution
{
    public static bool IsUserAssumed(ConditionType type) => type switch
    {
        // Enemy/positional state the API never exposes -> the user tells us (default unmet).
        ConditionType.VsIsolated
            or ConditionType.VsDebuffed
            or ConditionType.MeleeRangeCast
            or ConditionType.HpBelow      // target low-HP: enemy current HP is not exposed
            or ConditionType.StackGte     // enemy stack counts (blight/adoration/…) not exposed
            or ConditionType.HitsWall     // (M28) terrain contact — not tracked at all
            or ConditionType.InBrush      // (§76) brush position — same terrain story as HitsWall
            or ConditionType.Upgraded     // (loop 479) evolution/awakening — no API reports it
            or ConditionType.SweetSpot    // (loop 483) cast geometry — no position data at all
            or ConditionType.FromBehind   // (loop 485) caster position — same story
            => true,
        // Resolvable from the active player's own live snapshot, or a combo-sequence fact.
        ConditionType.ResourceGte         // own resource (ResourceValue)
            or ConditionType.ManaGte      // own mana
            or ConditionType.EveryNth     // combo-sequence ordinal
            or ConditionType.OnHitEmpowered // combo-sequence cast latch
            => false,
        _ => false,
    };
}

/// <summary>
/// The authoritative combo node shape (01_SYSTEM_ARCHITECTURE.md 4.1 Combo Node Graph),
/// plus two fields this module adds as a documented translation layer for M05's already-
/// built pipeline (<see cref="ExecutePercent"/>, <see cref="SpecialPropertyKey"/>) — see
/// <see cref="ComboEngine"/>'s <c>BuildDamageNode</c>/<c>EvaluateCondition</c> docs, and
/// the M03 agent report's Notes for Reviewer, for why each was necessary.
/// </summary>
/// <param name="ExecutePercent">Percentage (0-1) used by <see cref="ComboExecuteType.CurrentHp"/>/
/// <see cref="ComboExecuteType.MissingHp"/>. Not part of architecture's literal Data Model list
/// (which has no equivalent field); added for the same reason M05 added its own
/// <c>Damage.ComboNode.ExecutePercent</c> — the pipeline cannot implement those two execute
/// types without a percentage value from somewhere.</param>
/// <param name="SpecialPropertyKey">Optional key into
/// <see cref="ChampionRepository.GetSpecialProperty"/>, used only by
/// <see cref="ConditionType.StackGte"/> evaluation when a node's stack condition depends on
/// a champion-specific formula (e.g. a level-scaled passive stack value) rather than a
/// literal number. Not part of architecture's literal Data Model list; this is the generic,
/// non-champion-branching hook the M03 Agent Implementation Notes explicitly ask for.</param>
/// <param name="UserBonusEffects">Bonus effects the user manually attached to THIS node in the combo
/// editor (T3.3/T8), for bonuses that could not be auto-applied. Each references a champion skill's BIN
/// calc (never a typed-in number), so <see cref="ComboRunner"/> resolves it LIVE and appends per-type-
/// mitigated hits to this node — merged alongside the champion's curated bonus effects. Serialized with
/// the node so it survives save/reload; null (default) means none, keeping older combos unchanged.</param>
/// <param name="AttachedItemId">Data Dragon item id the user drag-attached to THIS node in the combo
/// editor (M04 "Pending User-Reported Changes" item 3 / LEAD DECISION loop 38 continuation 12).
/// Originally UX/organization only (records WHERE the user conceptually intends the item to matter,
/// rendered as a small sub-icon on the node) — as of loop 48, real engine behavior for items
/// <see cref="ItemEffectDb.Get"/> classifies as <see cref="ItemTrigger.ManualActiveBurst"/> (Deathfire
/// Grasp, 3128): <see cref="ComboEngine"/>'s <c>ApplyAttachedItemEffects</c> reads it and applies both
/// the item's instant burst and its damage-amplification window — see that method's doc comment. Every
/// OTHER attached item (uncovered id, or covered with a different trigger — e.g. Zhonya's) remains
/// exactly the original decorative-only behavior, so this field is still backward-compatible for every
/// pre-loop-48 use. Optional, null (default) means none, keeping older combos unchanged. The sibling
/// rune half of this feature is <see cref="AttachedRuneId"/> (implemented loop 50).</param>
/// <param name="AttachedRuneId">Data Dragon rune (perk) id the user drag-attached to THIS node in the
/// combo editor (Part 3 "advanced option" — the rune sibling of <see cref="AttachedItemId"/>).
/// FORCE-APPLIED (theorycraft): unlike the auto-apply path (<see cref="ComboRunner"/>'s equipped-rune
/// detection, which only reflects runes the player REALLY has and whose real trigger condition is
/// genuinely met), an attached rune's effect is applied to THIS node unconditionally, whether or not
/// it is equipped and whether or not its normal trigger condition is satisfied — the user is
/// explicitly asking "compute as if this rune fired here." <see cref="ComboEngine"/>'s
/// <c>ApplyAttachedRuneEffects</c> reads it: an additive rune (any id with a
/// <see cref="Runes.RuneEffectDb.Get"/> formula — the 8 manual + 5 of the 6 auto-trigger runes)
/// becomes a NEW trailing bonus node right after this one; Axiom Arcanist (8224, a pure damage
/// MULTIPLIER, no additive formula) instead scales this node's own damage fields by its ultimate
/// multiplier when this node is the ultimate. Optional, null (default) means none, keeping older
/// combos unchanged.</param>
/// <param name="UserHitDurationSeconds">Seconds the user manually says the target was actually
/// exposed to THIS node's duration-scaled hit (the combo editor's "적중시간" control — new
/// node-option feature, mirrors <see cref="AttachedItemId"/>'s optional-field pattern: nullable,
/// serialized with the node, backward-compatible). Only meaningful when the node's curated skill has
/// a <see cref="SkillHit.IsDurationScaled"/> hit (see <see cref="SkillDamageDb.GetDurationScaledHit"/>);
/// ignored otherwise. Null (default) = unset = 0 damage from that hit, the same honest P2 default as
/// this project's prior convention of omitting an escapable zone/DoT entirely rather than assuming
/// full duration — see <see cref="ComboRunner"/>'s duration-scaled hit resolution.</param>
/// <param name="UserAttackCount">(M22 Phase 3) How many hits/auto-attacks the user assumes a SUMMON/
/// pet lands (the combo editor's "몇 대" control). Only meaningful when the node's curated skill has
/// a per-attack hit (<see cref="SkillHit.PerAttackCalc"/>, e.g. Annie R Tibbers, Ivern Daisy); damage
/// = per-hit BIN number × this count. Nullable/serialized/backward-compatible, same optional-field
/// pattern as <see cref="UserHitDurationSeconds"/>. Null/0 (default) = 0 damage from that hit, the
/// honest P2 default (a summon's total output is player-uptime-dependent, not a fixed cast number).</param>
/// <param name="UserDistanceFraction">(M24 P3) How far, 0-1, the user says a DISTANCE/charge-scaled
/// hit (<see cref="SkillHit.IsDistanceScaled"/>, e.g. Hecarim E charge, Fizz R throw) travelled/charged:
/// 0 = minimum (the hit's <see cref="SkillHit.MinCalc"/>), 1 = full (its <see cref="SkillHit.Calc"/>),
/// linearly interpolated between. Nullable/serialized/backward-compatible, same optional-field pattern
/// as <see cref="UserHitDurationSeconds"/>. Null (default) = the MAX end, preserving a champion curated
/// at full charge (e.g. Hecarim E) so the resolved number is unchanged; the uncertainty RANGE still
/// spans [min, max] via ComboRunner's exposure-graph. Ignored for a non-distance-scaled hit.</param>
/// <param name="UserConditionMet">(M25 §11.G) For a CONDITIONAL-BONUS hit
/// (<see cref="SkillHit.IsConditional"/>) whose condition is UserAssumed (enemy/positional state the
/// API can't observe — VsIsolated/VsDebuffed/MeleeRangeCast/target-HpBelow), whether the user says the
/// condition holds: true = met (the hit's <see cref="SkillHit.MetCalc"/>, RangeMax end), false/null =
/// unmet (its baseline <see cref="SkillHit.Calc"/>, the conservative RangeMin anchor, P2). Null
/// (default) = unmet. Nullable/serialized/backward-compatible, same pattern as
/// <see cref="UserDistanceFraction"/>; the exposure-graph spans [unmet, met]. Ignored for an
/// AutoResolvable condition (own resource — read live from stats) and for a non-conditional hit.</param>
/// <param name="UserStackCount">(M25 §11.G) How many buff STACKS the user says the target/caster
/// carries for a STACK-SCALED hit — a curated calc with a BuffCounter per-stack term (e.g. Nasus Q
/// Siphoning-Strike stacks, each +1 damage). The engine can't observe live buff-stack counts, so the
/// count is a user knob threaded into <see cref="SkillDamage.ComputeCalcDamage"/> →
/// <see cref="ChampionDb.FormulaInterpreter"/>. Null (default) = 0 = the conservative un-stacked floor
/// (P2, never over-states — the same honest default as <see cref="UserAttackCount"/>); the stack term
/// is unbounded in-game so, like the "몇 대" knob, it does NOT auto-widen the range. Nullable/
/// serialized/backward-compatible; ignored by any hit whose calc has no stack term.</param>
/// <param name="CanCrit">(M27) Threaded from <see cref="SkillHit.CanCrit"/> for a curated ability hit
/// that can crit (e.g. Garen E). <see cref="ComboEngine.BuildDamageNode"/> ORs this with its existing
/// AA-only rule. Default false = no behavior change for any non-crittable hit.</param>
/// <param name="CritDamageScalar">(M27) Threaded from <see cref="SkillHit.CritDamageScalar"/> — the
/// fraction of a full crit's bonus this hit deals (1.0 = full crit, same as an AA). Only meaningful
/// when <see cref="CanCrit"/> is true. Default 1.0.</param>
public sealed record ComboNode(
    string Id,
    ComboNodeType NodeType,
    string Name,
    double Cooldown,
    double Mana,
    double Damage,
    ComboDamageType DamageType,
    double RatioAD,
    double RatioBonusAD,
    double RatioAP,
    double CastTime,
    double Delay,
    double TravelTime,
    Condition? Condition = null,
    double? Stack = null,
    double? MaxStack = null,
    ComboExecuteType ExecuteType = ComboExecuteType.None,
    double? ExecuteThreshold = null,
    int Priority = 0,
    double ExecutePercent = 0,
    string? SpecialPropertyKey = null,
    IReadOnlyList<AttachableBonusEffect>? UserBonusEffects = null,
    string? AttachedItemId = null,
    double? UserHitDurationSeconds = null,
    string? AttachedRuneId = null,
    int? UserAttackCount = null,
    double? UserDistanceFraction = null,
    bool? UserConditionMet = null,
    int? UserStackCount = null,
    bool CanCrit = false,
    double CritDamageScalar = 1.0,
    // (§27 (B)) Dynamic low-HP on-hit gate fraction (0-1), threaded from SkillHit.HpBelowGate to the
    // M05 node's HpBelowGateFraction in BuildDamageNode. Null (default) = ungated.
    double? HpBelowGate = null);

/// <summary>An implicit or (future V2) explicit sequencing edge between two node ids.
/// MVP's <see cref="ComboEngine.BuildGraph"/> always populates this as the linear
/// node[i]-&gt;node[i+1] chain; <see cref="ComboEngine.Execute"/> does not read it at all
/// (it walks <see cref="ComboGraph.Nodes"/> in array order) — Edges exists purely so a
/// future branching traversal has a real data structure to extend instead of a rewrite
/// (spec design note).</summary>
public sealed record ComboEdge(string FromNodeId, string ToNodeId);

/// <summary>MVP: a linear sequence (<see cref="Nodes"/>, walked in order by
/// <see cref="ComboEngine.Execute"/>) plus an edge list kept for V2 extensibility only.</summary>
public sealed record ComboGraph(IReadOnlyList<ComboNode> Nodes, IReadOnlyList<ComboEdge> Edges);

/// <summary>Spec's own Data Model. <see cref="Attacker"/>/<see cref="Defender"/> are
/// M05's real types (not a Combo-Engine-local copy) per the spec text
/// ("attacker: AttackerStat // M05 참조"); <see cref="UserRuneConfig"/> is M06's real type.
/// <see cref="CasterMaxHealth"/>/<see cref="CasterIsMelee"/> are additive fields (default 0/false,
/// so every pre-existing 4-positional-arg construction site keeps compiling unchanged) needed only
/// to evaluate a manual rune's %-max-health damage terms (Grasp of the Undying, Shield Bash — see
/// RuneEffectDb) via <see cref="RuneCasterStats"/>; M05's own <see cref="AttackerStat"/> carries
/// neither field, so they can't be sourced from <see cref="Attacker"/> — <c>ComboRunner.BuildContext</c>
/// populates them from the live snapshot's <c>ActivePlayerStats.MaxHealth</c> and the same
/// <c>ComboRunner.IsMelee</c> champion-attack-range check already used for item procs.</summary>
public sealed record ExecutionContext(
    string ChampionId,
    AttackerStat Attacker,
    DefenderStat Defender,
    UserRuneConfig UserRuneConfig,
    double CasterMaxHealth = 0,
    bool CasterIsMelee = false);

/// <summary>One entry of <see cref="ComboResult.NodeBreakdown"/>: <see cref="Damage"/> comes
/// from <see cref="DamageCalcResult.Breakdown"/>, <see cref="Delay"/> passes through the
/// node's own architecture <c>delay</c> field untouched.</summary>
public sealed record NodeBreakdownEntry(string NodeId, double Damage, double Delay);

/// <summary>
/// (M20 §6.2 / CLAUDE_CODE_TODO §75) What <see cref="ComboResult.BurstCeilingDamage"/> means for
/// this combo. Display-only classification — P3/P4 unchanged, nothing here reorders or inputs.
/// </summary>
public enum BurstCeilingStatus
{
    /// <summary>No HP-basis (variance-heavy) node in the combo — the authored total is the whole
    /// story and no ceiling is reported.</summary>
    None,

    /// <summary>One variance node, and moving it beats the authored order:
    /// <see cref="ComboResult.BurstCeilingDamage"/> is the kill-feasible maximum and
    /// <see cref="ComboResult.BurstCeilingSequence"/> is where it belongs.</summary>
    Optimized,

    /// <summary>One variance node and the user already authored it at its optimal placement —
    /// the ceiling equals the headline total. Worth saying rather than hiding, so the absence of
    /// a suggestion reads as "confirmed", not "not computed".</summary>
    AlreadyOptimal,

    /// <summary>The combo kills with the variance node REMOVED, so its ceiling is not a
    /// requirement — M20 §6.2's degenerate case, displayed as "R unnecessary" rather than as a
    /// fabricated ceiling.</summary>
    VarianceUnnecessary,

    /// <summary>Two or more independently movable variance nodes. §6.2's closed form is derived
    /// for one, so no optimization is attempted and the authored number stands, labelled as such
    /// (CLAUDE_CODE_TODO §75-4: never silently approximate).</summary>
    MultipleVarianceNodes,
}

/// <summary>Spec's own Data Model. Only executed (non-skipped) nodes contribute to
/// <see cref="TotalMana"/>/<see cref="TotalCastTime"/>/<see cref="NodeBreakdown"/> —
/// skipped nodes contribute 0 to all three (spec's "애니메이션 취소 등은 MVP 범위 밖"
/// simplicity note, extended symmetrically to skip handling).
///
/// <para><see cref="TotalDamageMin"/>/<see cref="TotalDamageMax"/> (M05 v2.8) are threaded
/// straight through from <see cref="DamageCalcResult.TotalDamageMin"/>/<c>TotalDamageMax</c> —
/// see that type's doc comment for exactly what Min/Max mean and how <see cref="IsLethal"/>'s
/// semantics changed alongside them (now Max-track/best-case based, not blend-based).</para>
///
/// <para><see cref="FinisherNodeLabel"/>/<see cref="SuffixThresholdHP"/> (M20 §6 item 3 / §6.3,
/// suffix kill-threshold): the LAST executed node whose <see cref="ComboExecuteType"/> is
/// HP-scaling (<c>MissingHp</c>/<c>CurrentHp</c>/<c>BaseWithMissingHpBonus</c> — e.g. Garen R),
/// and the HP the target must be at (or below) when that node is cast for the remaining tail of
/// the combo to exactly kill (<c>DamageEngine.SolveSuffixThresholdHp</c>). Both null/optional —
/// same additive-field pattern as <see cref="TotalDamageMin"/>/<c>TotalDamageMax</c> above, so
/// every existing construction site keeps compiling unchanged — and populated together: null
/// when the combo has no HP-scaling node, or when the fold can't apply to that node's suffix
/// (Threshold node / nonzero shield in the tail).</para>
///
/// <para><see cref="RuneHeal"/> (auto-rune-trigger-engine batch): attacker healing granted by
/// an auto-triggered rune's own kit — currently only Conqueror's full-stack heal (8% melee/5%
/// ranged of <see cref="TotalDamage"/>, applied only when the combo's executed damaging-hit
/// count genuinely reaches the full-stack threshold — see <see cref="ComboEngine.ApplyRemainingAutoTriggeredRunes"/>).
/// Deliberately a NEW field rather than folded into <see cref="Damage.DamageCalcResult.LifestealHeal"/>:
/// that field is itemization/spell lifesteal, a semantically distinct heal source, and (same as
/// this field) is not itself threaded any further than <see cref="Damage.DamageCalcResult"/> today
/// — no existing caller reads it off <see cref="ComboResult"/> or the HUD, so adding a same-shaped,
/// separately-named field here is the smallest surgical change that doesn't conflate the two heal
/// sources. Default 0 (additive field, every pre-existing construction site keeps compiling
/// unchanged). NOTE: wiring this to the HUD card (<c>OverlayHost.cs</c>) is an intentional,
/// flagged follow-up, NOT done in this batch — see Agent Report.
///
/// (M23 Phase 2 Step 5 taxonomy) Source=Rune, Trigger=Self, Condition=SelfBuffActive (Conqueror
/// fully stacked) — archetype A4. Computed in Stage B (see <see cref="AutoTriggeredRuneIds"/>'s
/// doc comment for why Conqueror can't be an appended Stage-A hit).</para>
///
/// <para><see cref="BurstCeiling"/>/<see cref="BurstCeilingDamage"/>/<see cref="BurstCeilingNodeLabel"/>/
/// <see cref="BurstCeilingSequence"/> (M20 §6.2, CLAUDE_CODE_TODO §75): the kill-feasible MAXIMUM
/// total damage — what the combo deals when its single variance-heavy (HP-basis) cast is placed at
/// the latest point the target is still alive, rather than wherever the user happened to author it.
/// <see cref="TotalDamage"/> is UNCHANGED and still the authored-order number; these are a NEW,
/// additive second reading and BOTH are reported (the ceiling never overwrites the authored total).
/// See <see cref="BurstCeilingStatus"/> for what each outcome means, and
/// <c>DamageEngine.SolveKillFeasibleMaxDamage</c> for the search itself. Display only (P3/P4) — the
/// overlay may show the better placement, it never reorders, queues or inputs anything.</para>
/// </summary>
public sealed record ComboResult(
    double TotalDamage,
    double TotalMana,
    bool ManaSufficient,
    double KillThresholdHP,
    bool IsLethal,
    IReadOnlyList<NodeBreakdownEntry> NodeBreakdown,
    double TotalCastTime,
    double TotalDamageMin = 0,
    double TotalDamageMax = 0,
    string? FinisherNodeLabel = null,
    double? SuffixThresholdHP = null,
    double RuneHeal = 0,
    double RangeMin = 0,
    double RangeMax = 0,
    double ExecuteThresholdHP = 0,
    string? ExecuteRuleLabel = null,
    BurstCeilingStatus BurstCeiling = BurstCeilingStatus.None,
    double BurstCeilingDamage = 0,
    string? BurstCeilingNodeLabel = null,
    string? BurstCeilingSequence = null)
{
    /// <summary>(M24 P1) Alias for <see cref="TotalDamageMin"/> — the CRIT-only floor (the no-crit
    /// track). Named for clarity now that <see cref="RangeMin"/> is the composed uncertainty floor:
    /// <c>CritMin</c> is ONE axis (crit), <c>RangeMin</c> is the unified crit ⊗ condition/knob floor.</summary>
    public double CritMin => TotalDamageMin;

    /// <summary>(M24 P1) Alias for <see cref="TotalDamageMax"/> — the CRIT-only ceiling (the all-crit
    /// track). See <see cref="CritMin"/>.</summary>
    public double CritMax => TotalDamageMax;
}
