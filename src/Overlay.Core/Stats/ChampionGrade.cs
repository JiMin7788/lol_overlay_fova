namespace Overlay.Core.Stats;

/// <summary>One champion's place in the tier list: the composite point score, the confidence-adjusted
/// score the grade is actually cut on, the grade, and whether the confidence adjustment is what capped
/// it. <see cref="Gated"/> lets the view explain a row whose point score would rank a tier higher than
/// its letter.</summary>
public sealed record GradedRow(TierRow Row, double Score, double LowerEdge, string Grade, bool Gated);

/// <summary>
/// The tier list's grading: a self-contained "PS-style" composite power score (loop 538, user
/// request), replacing the pure win-rate edge of loop 467. It combines win rate, pick rate and ban
/// rate into one number the way a Korean tier site's PS score does, so a 51%-but-everywhere pick reads
/// as stronger than a 53%-but-niche one — which the win-rate-only grade could not express.
///
/// <para><b>Calibration.</b> The formula is fit by least squares to a 258-champion snapshot of that
/// site (<c>ps_star_258.csv</c>, 2026-08): PS ≈ a + b·WR + c·√PickRate + d·√BanRate, and every other
/// step (the sample-confidence weight, the standard-error slope, the 1.28-σ lower bound) is fit to the
/// site's own columns. Model-vs-site correlation ≈ 0.94, mean |Δ| ≈ 1 point. It is OUR model — the
/// numbers are computed from our own KR aggregation, not scraped — so it can differ from the site by
/// the residual the public win/pick/ban rates cannot explain. Percentages throughout, not fractions.</para>
///
/// <para><b>Two numbers behind a letter</b>, both printed by the view:</para>
/// <para><see cref="Score"/> — PS on the SAMPLE-SHRUNK win rate (a thin hot streak cannot inflate it).</para>
/// <para><see cref="LowerEdge"/> — PS★, that point score minus 1.28 standard errors (the SE mapped to
/// PS-scale points). The GRADE is cut on THIS, so a small sample is graded on what it conservatively
/// supports; the confidence "gate" is inherent — no separate rule — because a thin sample's SE is large
/// enough to pull PS★ down a tier on its own.</para>
/// </summary>
public static class ChampionGrade
{
    // PS ≈ a + b·WR% + c·√PickRate% + d·√BanRate% — least-squares fit to the site snapshot (R² ≈ 0.87).
    private const double PsBase = -32.2514, PsWin = 1.546, PsPick = 2.0675, PsBan = 0.3634;

    // Sample-confidence weight √N/(√N+K0), K0 fit to the site's own 신뢰가중치 column (maxerr 0.007).
    private const double WeightK = 8.7;

    // Win-rate SE → PS-scale slope: |SeA − SeB·(WR−50)| PS points per WR point (the site's ⑤), times
    // the win-rate SE, times a calibration factor fit to the site's 오차 column.
    private const double SeSlopeA = 1.4731, SeSlopeB = 0.08068, SeFactor = 0.96;

    // PS★ = point − 1.28·SE: a one-sided ~90% lower confidence bound (the site's ⑥).
    private const double ZLower = 1.28;

    /// <summary>Bayesian shrink toward 50%, kept for the view's separate win-rate SORT (which still
    /// ranks by shrunk win rate, not by the composite). Unrelated to the grade's own weight.</summary>
    public const double SmoothK = 10;

    /// <summary>Grades, best first, as the minimum PS★ (the confidence-adjusted composite). ABSOLUTE
    /// cutoffs — a lane can hold no S+ at all, which is the healthy-meta norm. Calibrated to a sensible
    /// tier pyramid on the 258-champion sample (S+ ≈ top 2%, S ≈ 8%, A ≈ 16%, B the bulk, then C/D).
    /// The PS scale centres near 50, so ~50 = an average champion (B), and 56+ = the few genuine OPs.</summary>
    public static readonly (string Label, double MinPsStar)[] Bands =
    {
        ("S+", 56.0),
        ("S", 53.0),
        ("A", 50.5),
        ("B", 48.0),
        ("C", 45.5),
        ("D", double.NegativeInfinity),
    };

    private static double ConfidenceWeight(int games)
        => games <= 0 ? 0 : System.Math.Sqrt(games) / (System.Math.Sqrt(games) + WeightK);

    private static double Ps(double winPct, double pickPct, double banPct)
        => PsBase + PsWin * winPct
           + PsPick * System.Math.Sqrt(System.Math.Max(0, pickPct))
           + PsBan * System.Math.Sqrt(System.Math.Max(0, banPct));

    /// <summary>The composite point score (PS on the sample-shrunk win rate). Higher is stronger; the
    /// scale centres near 50. Returns 0 for an empty row (never ranked).</summary>
    public static double Score(TierRow row)
    {
        if (row.Games <= 0) return 0;
        double win = row.WinRate * 100, pick = row.PickRate * 100, ban = row.BanRate * 100;
        double shrunkWin = 50 + (win - 50) * ConfidenceWeight(row.Games);
        return Ps(shrunkWin, pick, ban);
    }

    /// <summary>The confidence-adjusted score PS★ = <see cref="Score"/> − 1.28 standard errors, the
    /// SE being the win-rate SE mapped to PS-scale points. This is what the grade is cut on.</summary>
    public static double LowerEdge(TierRow row)
    {
        if (row.Games <= 0) return 0;
        double win = row.WinRate * 100, p = row.WinRate;
        double se = System.Math.Sqrt(System.Math.Max(0, p * (1 - p)) / row.Games) * 100; // %p
        double err = System.Math.Abs(SeSlopeA - SeSlopeB * (win - 50)) * se * SeFactor;  // PS points
        return Score(row) - ZLower * err;
    }

    private static string BandOf(double psStar)
    {
        for (int i = 0; i < Bands.Length; i++)
            if (psStar >= Bands[i].MinPsStar) return Bands[i].Label;
        return Bands[^1].Label;
    }

    /// <summary>The grade for one champion, on its own — no peer group involved. Cut on PS★, so the
    /// sample confidence is already baked in. <paramref name="gated"/> is true when the POINT score
    /// would place it a tier higher, i.e. the sample's uncertainty is what held the letter down.</summary>
    public static string Of(TierRow row, out bool gated)
    {
        if (row.Games <= 0) { gated = false; return Bands[^1].Label; }
        string byStar = BandOf(LowerEdge(row));
        gated = BandIndex(BandOf(Score(row))) < BandIndex(byStar);
        return byStar;
    }

    /// <inheritdoc cref="Of(TierRow, out bool)"/>
    public static string Of(TierRow row) => Of(row, out _);

    /// <summary>Position of a grade in <see cref="Bands"/>, best first; past the end for an unknown
    /// label so it sorts last rather than throwing.</summary>
    public static int BandIndex(string grade)
    {
        for (int i = 0; i < Bands.Length; i++)
            if (Bands[i].Label == grade) return i;
        return Bands.Length;
    }

    /// <summary>Grades a set of rows and orders them by GRADE first, then point score. The order is
    /// presentation only — no champion's grade depends on which others were passed in.</summary>
    public static IReadOnlyList<GradedRow> Rank(IReadOnlyList<TierRow> rows)
    {
        var result = new List<GradedRow>(rows.Count);
        foreach (var row in rows)
        {
            string grade = Of(row, out bool gated);
            result.Add(new GradedRow(row, Score(row), LowerEdge(row), grade, gated));
        }

        // Grade first (the confidence gate can drop a high-point champion a tier, so score alone would
        // interleave the tiers), then point score, then sample size as the final tie-break.
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
