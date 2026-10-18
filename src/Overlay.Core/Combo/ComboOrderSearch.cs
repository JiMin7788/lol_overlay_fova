using System.Text.RegularExpressions;

namespace Overlay.Core.Combo;

/// <summary>(M44) The other game-possible casting orders of a combo sequence, for the editor's "a
/// stronger order exists" hint. Pure: the caller scores each order (the real pipeline, via
/// ComboRunner.ComputePreview) and this class only decides which orders are worth trying.
///
/// <para>Ability chips and basic attacks move; Flash, Ignite and the items chip stay where the user
/// put them (Flash's position decides dash-triggered runes, the items chip fires where it sits).
/// Chips of the same ability keep their relative order, so Q → Q2 → Q3 never inverts. A form or
/// combined-cast slot (QCougar, ECannon, EQ) means no search at all — a form swap is a barrier
/// this does not model.</para></summary>
public static class ComboOrderSearch
{
    /// <summary>More distinct orders than this → no suggestion (each costs one pipeline run).</summary>
    public const int MaxOrders = 2000;

    private static readonly Regex MovableSlot = new(@"^(AA|P|[QWER][2-9]?)$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> FixedSlots = new(StringComparer.Ordinal)
        { "Flash", "Ignite", ComboEditor.ItemsNodeId };

    /// <summary>Every distinct allowed order as an index permutation (<c>order[i]</c> = the original
    /// index of the chip at position i), the current order first. Null when the sequence holds a slot
    /// the search does not model, or the orders exceed <see cref="MaxOrders"/>.</summary>
    /// <param name="slots">Each chip's slot id.</param>
    /// <param name="keys">Each chip's identity: equal keys are interchangeable (same slot, same knobs).</param>
    public static IReadOnlyList<int[]>? Orders(IReadOnlyList<string> slots, IReadOnlyList<string> keys)
    {
        if (slots.Count != keys.Count) throw new ArgumentException("slots and keys differ in length");
        var movable = new List<int>();
        for (int i = 0; i < slots.Count; i++)
        {
            if (MovableSlot.IsMatch(slots[i])) movable.Add(i);
            else if (!FixedSlots.Contains(slots[i])) return null;
        }

        // Same-letter chips form a chain consumed front to back; each AA is its own chain (free).
        var chains = new List<List<int>>();
        var byLetter = new Dictionary<char, List<int>>();
        foreach (int i in movable)
        {
            if (slots[i] == "AA") { chains.Add(new List<int> { i }); continue; }
            if (!byLetter.TryGetValue(slots[i][0], out var chain))
            {
                chain = new List<int>();
                byLetter[slots[i][0]] = chain;
                chains.Add(chain);
            }
            chain.Add(i);
        }

        var results = new List<int[]> { Enumerable.Range(0, slots.Count).ToArray() };
        var seen = new HashSet<string>(StringComparer.Ordinal) { Signature(results[0], keys) };
        var next = new int[chains.Count];
        var placed = new int[movable.Count];
        bool overflow = false;

        void Fill(int pos)
        {
            if (overflow) return;
            if (pos == movable.Count)
            {
                var order = Enumerable.Range(0, slots.Count).ToArray();
                for (int k = 0; k < movable.Count; k++) order[movable[k]] = placed[k];
                if (seen.Add(Signature(order, keys)))
                {
                    if (results.Count >= MaxOrders) { overflow = true; return; }
                    results.Add(order);
                }
                return;
            }
            // Interchangeable chips: try one of each key per position, or the tree repeats itself.
            var triedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int c = 0; c < chains.Count; c++)
            {
                if (next[c] == chains[c].Count) continue;
                int chip = chains[c][next[c]];
                if (!triedKeys.Add(keys[chip])) continue;
                placed[pos] = chip;
                next[c]++;
                Fill(pos + 1);
                next[c]--;
                if (overflow) return;
            }
        }

        Fill(0);
        return overflow ? null : results;
    }

    /// <summary>A better order: <see cref="Gain"/> more damage than <see cref="Current"/>, and whether
    /// that is enough to kill.</summary>
    public sealed record Hint(int[] Order, double Gain, double Current, bool Kills);

    /// <summary>The best of <paramref name="orders"/> (from <see cref="Orders"/>, current first), or
    /// null when none beats the current order by at least 1% and 1 damage.
    ///
    /// <para>Damage past the kill is not damage: the engine counts the killing hit in full and every
    /// later hit as zero, so "most damage" against a killable target only rewards casting the biggest
    /// spell last. So a current order that already kills gets no hint (every order that kills is as
    /// good on this target), and a gain is capped at the target's HP.</para></summary>
    /// <param name="score">Damage and the target's HP (current + shield) for this order; null when it
    /// could not be computed.</param>
    public static Hint? Best(IReadOnlyList<int[]> orders,
        Func<IReadOnlyList<int>, (double Damage, double TargetHp)?> score, CancellationToken ct = default)
    {
        if (orders.Count < 2 || score(orders[0]) is not { } cur || cur.TargetHp <= 0) return null;
        if (cur.Damage >= cur.TargetHp) return null;

        (int[] Order, double Damage)? top = null;
        foreach (var order in orders.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            if (score(order) is { } s && s.Damage > (top?.Damage ?? cur.Damage)) top = (order, s.Damage);
        }
        if (top is not { } t) return null;
        double gain = Math.Min(t.Damage, cur.TargetHp) - cur.Damage;
        if (gain < 1.0 || gain < cur.Damage * 0.01) return null;
        return new Hint(t.Order, gain, cur.Damage, t.Damage >= cur.TargetHp);
    }

    private static string Signature(int[] order, IReadOnlyList<string> keys)
        => string.Join("\u001f", order.Select(i => keys[i]));
}
