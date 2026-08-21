using Overlay.Core.Damage;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for M26 §6 Trace Mode T1 (CLAUDE_CODE_TODO.md §15) — <see cref="CalcTrace"/>
/// plus its DamageEngine hook. Verifies: a Begin→Execute→End block records exactly one engine Row
/// per node (Blend track only — Min/Max must never leak in); Row is a no-op while no block is open.
/// </summary>
public class CalcTraceTests
{
    public CalcTraceTests()
    {
        // Defensive: drop any block left open by a prior test/crash so each test starts clean.
        CalcTrace.End();
    }

    [Fact]
    public void Row_IsNoOp_WhenNotCollecting()
    {
        Assert.False(CalcTrace.IsCollecting);
        CalcTrace.Row("should be dropped, no block is open");
        Assert.Null(CalcTrace.End()); // nothing was ever begun -> still null
    }

    [Fact]
    public void Row_IsNoOp_AfterEndCloses_TheBlock()
    {
        CalcTrace.Begin("combo-closed", "header");
        Assert.NotNull(CalcTrace.End());

        CalcTrace.Row("dropped, block already closed");
        Assert.Null(CalcTrace.End());
    }

    [Fact]
    public void BeginExecuteEnd_RecordsExactlyOneEngineRowPerNode_BlendTrackOnly()
    {
        // 2 nodes, one CanCrit with a nonzero crit chance so Min/Max genuinely diverge from Blend —
        // if the hook leaked Min/Max rows too, this would produce 6 rows instead of 2.
        var nodes = new[]
        {
            new ComboNode("A", Damage: 10, RatioAD: 1.0, RatioAP: 0, DamageType: DamageType.Physical, CanCrit: true),
            new ComboNode("B", Damage: 20, RatioAD: 0, RatioAP: 1.0, DamageType: DamageType.Magic),
        };
        var attacker = new AttackerStat(Ad: 50, BonusAD: 20, Ap: 40, Level: 6, CriticalChance: 0.5, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 50, Mr: 30, Shield: 0);

        CalcTrace.Begin("combo-1", "attacker=Test defender=Dummy armor=50 mr=30 maxHp=1000");
        Assert.True(CalcTrace.IsCollecting);

        var engine = new DamageEngine();
        var result = engine.Calculate(new DamageCalcInput(nodes, attacker, defender));

        string? block = CalcTrace.End();
        Assert.False(CalcTrace.IsCollecting);
        Assert.NotNull(block);

        var engineRows = block!.Split(Environment.NewLine).Where(l => l.StartsWith("node=")).ToList();

        Assert.Equal(nodes.Length, engineRows.Count);
        Assert.Contains(engineRows, r => r.StartsWith("node=A "));
        Assert.Contains(engineRows, r => r.StartsWith("node=B "));
        Assert.Equal(nodes.Length, result.Breakdown.Count);

        // The computed numbers themselves are untouched by tracing (T1 is observation-only).
        Assert.True(result.TotalDamage > 0);
    }

    [Fact]
    public void CollectingFalse_BetweenBeginAndEnd_MeansNoBlockWasEverOpen()
    {
        // No Begin at all -> Row calls anywhere in a combo computation are inert.
        Assert.False(CalcTrace.IsCollecting);

        var nodes = new[]
        {
            new ComboNode("A", Damage: 10, RatioAD: 0, RatioAP: 0, DamageType: DamageType.True),
        };
        var attacker = new AttackerStat(Ad: 50, BonusAD: 0, Ap: 0, Level: 6, CriticalChance: 0, LifeSteal: 0);
        var defender = new DefenderStat(CurrentHP: 1000, MaxHP: 1000, Armor: 0, Mr: 0, Shield: 0);

        var engine = new DamageEngine();
        engine.Calculate(new DamageCalcInput(nodes, attacker, defender)); // must not throw/collect

        Assert.False(CalcTrace.IsCollecting);
        Assert.Null(CalcTrace.End()); // nothing accumulated
    }
}
