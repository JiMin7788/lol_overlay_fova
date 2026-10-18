using Overlay.Core.Combo;
using Xunit;

namespace Overlay.Core.Tests;

/// <summary>(M44) Which casting orders the combo editor tries.</summary>
public class ComboOrderSearchTests
{
    private static IReadOnlyList<int[]>? Orders(params string[] slots) => ComboOrderSearch.Orders(slots, slots);

    private static List<string> Spell(int[] order, string[] slots) => order.Select(i => slots[i]).ToList();

    [Fact]
    public void CurrentOrderComesFirst()
    {
        var orders = Orders("Q", "W", "E")!;
        Assert.Equal(new[] { 0, 1, 2 }, orders[0]);
        Assert.Equal(6, orders.Count);
    }

    [Fact]
    public void RecastsNeverInvert()
    {
        var slots = new[] { "Q", "Q2", "Q3", "E" };
        var orders = ComboOrderSearch.Orders(slots, slots)!;
        Assert.Equal(4, orders.Count); // E in any of 4 places, Q chain fixed
        foreach (var o in orders)
        {
            var seq = Spell(o, slots);
            Assert.True(seq.IndexOf("Q") < seq.IndexOf("Q2") && seq.IndexOf("Q2") < seq.IndexOf("Q3"));
        }
    }

    [Fact]
    public void FlashIgniteAndItemsStayPut()
    {
        var slots = new[] { "Flash", "Q", "AA", ComboEditor.ItemsNodeId, "R", "Ignite" };
        var orders = ComboOrderSearch.Orders(slots, slots)!;
        Assert.Equal(6, orders.Count); // Q, AA, R across three slots
        foreach (var o in orders)
        {
            Assert.Equal(0, o[0]);
            Assert.Equal(3, o[3]);
            Assert.Equal(5, o[5]);
        }
    }

    [Fact]
    public void IdenticalChipsAreTriedOnce()
    {
        // AA, AA, Q: 3 distinct orders, not 3! = 6.
        Assert.Equal(3, Orders("AA", "AA", "Q")!.Count);
    }

    [Fact]
    public void AttacksWithDifferentKnobsAreDistinct()
    {
        var slots = new[] { "AA", "AA", "Q" };
        var keys = new[] { "AA|1", "AA|3", "Q" };
        Assert.Equal(6, ComboOrderSearch.Orders(slots, keys)!.Count);
    }

    [Theory]
    [InlineData("QCougar")]
    [InlineData("ECannon")]
    [InlineData("EQ")]
    public void FormOrCombinedSlotsMeanNoSearch(string slot)
        => Assert.Null(Orders("Q", slot, "E"));

    [Fact]
    public void TooManyOrdersMeansNoSearch()
    {
        // 8 distinct AA knobs + nothing else = 8! = 40320 orders.
        var slots = Enumerable.Repeat("AA", 8).ToArray();
        var keys = Enumerable.Range(0, 8).Select(i => "AA|" + i).ToArray();
        Assert.Null(ComboOrderSearch.Orders(slots, keys));
    }

    [Fact]
    public void EveryOrderIsAPermutation()
    {
        var slots = new[] { "AA", "Q", "W", "AA", "E", "R" };
        foreach (var o in Orders(slots)!)
            Assert.Equal(Enumerable.Range(0, slots.Length), o.OrderBy(i => i));
    }

    // ── Best: kill line, not overkill ────────────────────────────────────────────────

    /// <summary>A fake pipeline: chip i deals dmg[i], doubled when chip <paramref name="amp"/> came earlier.</summary>
    private static Func<IReadOnlyList<int>, (double, double)?> Score(double[] dmg, double hp, int amp = -1)
        => order =>
        {
            double total = 0; bool amped = false;
            foreach (int i in order) { total += amped ? dmg[i] * 2 : dmg[i]; if (i == amp) amped = true; }
            return (total, hp);
        };

    [Fact]
    public void OverkillIsNotAGain()
    {
        // Q 400, W 300, E 600 on 1000 HP: every order kills on its third chip. Casting E last
        // "deals" more only past the kill, so there is nothing to suggest.
        var orders = Orders("Q", "W", "E")!;
        Assert.Null(ComboOrderSearch.Best(orders, Score(new[] { 400.0, 300, 600 }, 1000)));
    }

    [Fact]
    public void AnOrderThatAlreadyKillsGetsNoHint()
    {
        // W amps what follows: W Q E deals more than Q W E, but both kill 1100 HP.
        var orders = Orders("Q", "W", "E")!;
        Assert.Null(ComboOrderSearch.Best(orders, Score(new[] { 400.0, 300, 600 }, 1100, amp: 1)));
    }

    [Fact]
    public void ANonKillingOrderPrefersMoreDamage()
    {
        // 5000 HP: nothing kills. Q W E = 400+300+1200 = 1900; W first doubles both others = 2300.
        var orders = Orders("Q", "W", "E")!;
        var hint = ComboOrderSearch.Best(orders, Score(new[] { 400.0, 300, 600 }, 5000, amp: 1));
        Assert.NotNull(hint);
        Assert.False(hint!.Kills);
        Assert.Equal(1, hint.Order[0]);
        Assert.Equal(400, hint.Gain, 6);
    }

    [Fact]
    public void AGainIsCappedAtTheKill()
    {
        // 2000 HP: current 1900 does not kill, the amped order (2300) does — the gain is the 100 HP
        // that was missing, not the 400 the amped order "deals".
        var orders = Orders("Q", "W", "E")!;
        var hint = ComboOrderSearch.Best(orders, Score(new[] { 400.0, 300, 600 }, 2000, amp: 1));
        Assert.NotNull(hint);
        Assert.Equal(100, hint!.Gain, 6);
        Assert.True(hint.Kills);
    }
}
