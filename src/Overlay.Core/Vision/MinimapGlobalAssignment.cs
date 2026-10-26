namespace Overlay.Core.Vision;

/// <summary>How stage 2 gives champions to the frame's enemy-ring candidates (<see cref="DetectionOptions.Assignment"/>).</summary>
public enum AssignmentMode
{
    /// <summary>Each candidate takes its own argmax; one claim per champion is kept afterwards. Today's detector.</summary>
    PerCandidate,

    /// <summary>lolF04 (R16, TODO:425): one-to-one assignment over the whole frame, see <see cref="MinimapGlobalAssignment"/>.</summary>
    Global,
}

/// <summary>lolF04 (R16, TODO:425) frame-level global assignment: every enemy-ring candidate is scored against
/// every enemy template, and the one-to-one candidate→champion assignment with the largest summed score wins,
/// using only pairs scoring ≥ <see cref="DetectionOptions.ConfidenceThreshold"/>. So "the Seraphine icon takes
/// Sett and the real Sett is pushed out" loses on the total. It cannot help when the real Sett is not in the
/// frame: the wrong icon still takes Sett. Opt-in through <see cref="DetectionOptions.Assignment"/>.</summary>
public static class MinimapGlobalAssignment
{
    /// <summary>The mode production runs. lolF04 t5e measured Global and did not adopt it
    /// (docs/reports/minimap_lolF04_variants.md §TODO:425, "Result: measured, not adopted"), so production and
    /// the replay default stay PerCandidate.</summary>
    public static AssignmentMode Measured => AssignmentMode.PerCandidate;

    /// <summary>The variant's description: the replay prints it, and an adopted variant's start-up log line is
    /// "minimap: detector variant on — " + this (spec R14h).</summary>
    public static string Describe() => "global-assignment";

    /// <summary>The assignment for one frame. <paramref name="scores"/>[i][c] = candidate i's score against enemy
    /// template c (template order). Returns per candidate the assigned champion index (−1 = none), its margin =
    /// assigned score − the candidate's best score over champions not assigned to another candidate (NaN if there
    /// is none), and that runner-up champion's index (−1 with a NaN margin).
    ///
    /// <para>Exhaustive DP over (candidate, set of used champions). Ties between equal totals: the lower candidate
    /// index gets a champion first, then the earlier champion in template order.</para></summary>
    internal static (int[] Champion, double[] Margin, int[] RunnerUp) Assign(IReadOnlyList<double[]> scores, double threshold)
    {
        int n = scores.Count;
        int k = n == 0 ? 0 : scores[0].Length;
        // AI-NOTE: 2^k states per candidate; the roster has ≤ 5 enemy templates, the cap only stops a runaway table.
        if (k > 16) throw new ArgumentException($"at most 16 champions (got {k})", nameof(scores));
        int states = 1 << k;

        // best[i][mask] = the largest total candidates i..n-1 can add when the champions in mask are taken.
        var best = new double[n + 1][];
        best[n] = new double[states];
        for (int i = n - 1; i >= 0; i--)
        {
            best[i] = new double[states];
            for (int mask = 0; mask < states; mask++)
            {
                double b = best[i + 1][mask];
                for (int c = 0; c < k; c++)
                    if ((mask & (1 << c)) == 0 && scores[i][c] >= threshold)
                        b = Math.Max(b, scores[i][c] + best[i + 1][mask | (1 << c)]);
                best[i][mask] = b;
            }
        }

        // AI-NOTE: forward walk; the first option that reproduces the optimum is taken, champions in order before
        // "none" — that is the tie order. The comparison is exact: each value is the same double expression the
        // table was filled with.
        var champion = new int[n];
        int used = 0;
        for (int i = 0; i < n; i++)
        {
            champion[i] = -1;
            for (int c = 0; c < k; c++)
                if ((used & (1 << c)) == 0 && scores[i][c] >= threshold
                    && scores[i][c] + best[i + 1][used | (1 << c)] == best[i][used])
                {
                    champion[i] = c;
                    used |= 1 << c;
                    break;
                }
        }

        var margin = new double[n];
        var runnerUp = new int[n];
        for (int i = 0; i < n; i++)
        {
            margin[i] = double.NaN;
            runnerUp[i] = -1;
            if (champion[i] < 0) continue;
            for (int c = 0; c < k; c++)
                if ((used & (1 << c)) == 0 && (runnerUp[i] < 0 || scores[i][c] > scores[i][runnerUp[i]]))
                    runnerUp[i] = c;
            if (runnerUp[i] >= 0) margin[i] = scores[i][champion[i]] - scores[i][runnerUp[i]];
        }
        return (champion, margin, runnerUp);
    }
}
