namespace Overlay.Core.Stats;

/// <summary>One champion's place in the tier list: the score it was graded on, the conservative
/// edge behind the confidence gate, the grade itself, and whether that gate is what capped it.
/// <see cref="Gated"/> is why a row can show a high score under a middling letter, so the view has
/// to be able to say so.</summary>
public sealed record GradedRow(TierRow Row, double Score, double LowerEdge, string Grade, bool Gated);

/// <summary>
/// The tier list's grading: ABSOLUTE cutoffs on a measured win-rate edge (loop 467, replacing the
/// percentile bands of loop 464 at the user's direction — percentile bands fix how many champions
/// hold each letter, so a lane always had one S+ and roughly three S whatever the patch looked
/// like, which is a property of the ranking rather than of the champions).
///
/// <para>Grades can now be empty, crowded, or lopsided, and that is the point: an S+ means the
/// sample measured this champion at least <see cref="Bands"/>' first cutoff above even, not that
/// it came first.</para>
///
/// <para>The claim is absolute, so it has to be stated in measured units and be checkable. Both
/// numbers behind a letter are printed by the view:</para>
///
/// <para><see cref="Score"/> — the win-rate edge in percentage points, after shrinking toward 50%
/// with the project-wide K=10. This is what the cutoffs are compared against, so a reader can see
/// exactly why a champion sits where it does.</para>
///
/// <para><see cref="LowerEdge"/> — the same edge minus one standard error, i.e. what the sample
/// supports conservatively. This drives the CONFIDENCE GATE: the top two grades additionally
/// require the sample to place the champion above even, so a 95-game hot streak cannot buy an S.
/// The gate exists because this pipeline's per-champion samples are hundreds of games, not
/// millions — at that size a standard error is 3-4 percentage points, comparable to the entire
/// spread of real win rates, and a bare point estimate would hand out letters to noise.</para>
/// </summary>
public static class ChampionGrade
{
    /// <summary>Bayesian shrink toward 50%, the same constant the aggregations and the win-rate
    /// sort already use. Kept identical so two places never disagree about what a win rate means.</summary>
    public const double SmoothK = 10;

    /// <summary>Grades, best first, as the minimum win-rate edge in PERCENTAGE POINTS. Chosen
    /// against the measured distribution rather than by feel: across the five lanes of the 16.15
    /// Platinum+ sample these produce 2–6 S+ and 1–6 S per lane, with the count varying by lane —
    /// which is the behaviour percentile bands could not give.</summary>
    public static readonly (string Label, double MinEdge)[] Bands =
    {
        ("S+", 4.0),
        ("S", 2.5),
        ("A", 1.0),
        ("B", -1.0),
        ("C", -3.0),
        ("D", double.NegativeInfinity),
    };

    /// <summary>Grades at or above this one must also clear the confidence gate.</summary>
    private const int GatedBands = 2;   // S+ and S

    /// <summary>Where a champion demoted by the gate lands: the best grade that does not claim
    /// more than the sample supports.</summary>
    private const string DemotedTo = "A";

    /// <summary>Shrunk win rate, pulled toward 50% by <see cref="SmoothK"/> so a thin sample
    /// cannot report an extreme rate at face value.</summary>
    private static double Shrunk(TierRow row)
    {
        if (row.Games <= 0) return 0.5;
        double wins = row.WinRate * row.Games;
        return (wins + SmoothK * 0.5) / (row.Games + SmoothK);
    }

    /// <summary>Win-rate edge over even, in percentage points: +3.2 means "wins 53.2% of the time,
    /// after shrinking". This is the number the cutoffs are applied to.</summary>
    public static double Score(TierRow row)
        => row.Games <= 0 ? 0 : (Shrunk(row) - 0.5) * 100;

    /// <summary>The edge the sample supports conservatively — one standard error below
    /// <see cref="Score"/>. Positive means "this sample places the champion above even".</summary>
    public static double LowerEdge(TierRow row)
    {
        if (row.Games <= 0) return 0;
        double p = Shrunk(row);
        double se = Math.Sqrt(p * (1 - p) / (row.Games + SmoothK));
        return (p - se - 0.5) * 100;
    }

    /// <summary>The grade for one champion, on its own — no peer group involved. <paramref
    /// name="gated"/> reports whether the confidence gate is what produced it.</summary>
    public static string Of(TierRow row, out bool gated)
    {
        gated = false;
        double score = Score(row), lower = LowerEdge(row);
        for (int i = 0; i < Bands.Length; i++)
        {
            if (score < Bands[i].MinEdge) continue;
            // Confidence gate: the top grades claim more than a point estimate can carry at this
            // sample size, so they also require the conservative edge to be above even.
            if (i < GatedBands && lower <= 0)
            {
                gated = true;
                return DemotedTo;
            }
            return Bands[i].Label;
        }
        return Bands[^1].Label;
    }

    /// <inheritdoc cref="Of(TierRow, out bool)"/>
    public static string Of(TierRow row) => Of(row, out _);

    /// <summary>Position of a grade in <see cref="Bands"/>, best first; past the end for an
    /// unknown label so it sorts last rather than throwing.</summary>
    public static int BandIndex(string grade)
    {
        for (int i = 0; i < Bands.Length; i++)
            if (Bands[i].Label == grade) return i;
        return Bands.Length;
    }

    /// <summary>Grades a set of rows and orders them by GRADE first, then score. The order is
    /// presentation only — unlike the percentile scheme this replaced, no champion's grade depends
    /// on which other champions were passed in.
    ///
    /// <para>Grade has to lead the ordering because the confidence gate breaks the tie between the
    /// two: a gated champion keeps its high score while dropping a letter, so ordering on score
    /// alone drops an A into the middle of the S+ block. A tier list that interleaves its own tiers
    /// is not one.</para></summary>
    public static IReadOnlyList<GradedRow> Rank(IReadOnlyList<TierRow> rows)
    {
        var result = new List<GradedRow>(rows.Count);
        foreach (var row in rows)
        {
            string grade = Of(row, out bool gated);
            result.Add(new GradedRow(row, Score(row), LowerEdge(row), grade, gated));
        }

        // Within a grade, by score; ties (identical records score identically) break on sample
        // size, so the better-evidenced champion is listed first.
        result.Sort((a, b) =>
        {
            int ba = BandIndex(a.Grade), bb = BandIndex(b.Grade);
            if (ba != bb) return ba.CompareTo(bb);
            if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
            return b.Row.Games.CompareTo(a.Row.Games);
        });
        return result;
    }
}
