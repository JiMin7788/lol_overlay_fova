using Overlay.Core;
using Overlay.Core.Combo;

namespace Overlay.Core.Tests;

/// <summary>
/// Executable proof for the §40 combo-overlay Core helpers on <see cref="ComboRunner"/>:
/// <see cref="ComboRunner.EnemyRoster"/> (full enemy row incl. dead + respawn, for the portrait row)
/// and <see cref="ComboRunner.SkillDamageBySlot"/> (per-skill P/Q/W/E/R/A aggregation of a combo's
/// node breakdown, for the skill overlay).
/// </summary>
public class ComboOverlayHelpersTests
{
    private static GameSnapshot Snapshot(params (string champ, string team, bool dead, double respawn)[] rows)
    {
        var snap = new GameSnapshot
        {
            HasData = true,
            ActivePlayerRiotId = "Me#KR1",
            PlayerCount = rows.Length + 1,
        };
        snap.Players[0].RiotId = "Me#KR1";
        snap.Players[0].Team = "ORDER";
        snap.Players[0].ChampionName = "Me";
        for (int i = 0; i < rows.Length; i++)
        {
            snap.Players[i + 1].ChampionName = rows[i].champ;
            snap.Players[i + 1].Team = rows[i].team;
            snap.Players[i + 1].IsDead = rows[i].dead;
            snap.Players[i + 1].RespawnTimer = rows[i].respawn;
        }
        return snap;
    }

    [Fact]
    public void EnemyRoster_KeepsDeadEnemies_ExcludesAllies_ProjectsRespawn()
    {
        var snap = Snapshot(
            ("Zed", "CHAOS", false, 0),
            ("LeeSin", "CHAOS", true, 15.4),   // dead, respawning
            ("AllyGaren", "ORDER", false, 0),  // ally — excluded
            ("Jinx", "CHAOS", false, 0));

        var roster = ComboRunner.EnemyRoster(snap);

        Assert.Equal(3, roster.Count); // ally dropped, both living + dead enemies kept
        Assert.Equal("Zed", roster[0].ChampionName);
        Assert.False(roster[0].IsDead);
        Assert.Equal("LeeSin", roster[1].ChampionName);
        Assert.True(roster[1].IsDead);
        Assert.Equal(15.4, roster[1].RespawnTimer, 3);
        Assert.Equal("Jinx", roster[2].ChampionName);
    }

    [Fact]
    public void EnemyRoster_NoActivePlayerRow_ReturnsEmpty()
    {
        var snap = Snapshot(("Zed", "CHAOS", false, 0));
        snap.ActivePlayerRiotId = "Nobody#KR9";
        snap.Players[0].RiotId = "Nobody-else#KR1"; // active row unresolvable
        // Players[0] no longer matches → ResolveActive fails
        Assert.Empty(ComboRunner.EnemyRoster(snap));
    }

    [Fact]
    public void SkillDamageBySlot_AggregatesBySlot_FoldsAutoIntoA_ExcludesRuneItemNodes()
    {
        var nodes = new List<NodeBreakdownEntry>
        {
            new("Q_0", 200, 0),
            new("Q_0#h1c0", 40, 0),      // same Q, expanded per-hit → folds into Q
            new("W_1", 130, 0),
            new("R_0", 300, 0),
            new("AA_0", 62, 0),          // auto-attack → folds into A
            new("AA_1", 8, 0),
            new("P_0", 45, 0),           // passive damage → P
            new("autorune#electrocute", 90, 0), // rune node → excluded (no slot prefix)
            new("3153#item", 50, 0),     // item proc → excluded
        };
        var cr = new ComboResult(
            TotalDamage: 925, TotalMana: 0, ManaSufficient: true, KillThresholdHP: 0,
            IsLethal: false, NodeBreakdown: nodes, TotalCastTime: 0);

        var slots = ComboRunner.SkillDamageBySlot(cr);

        // exactly six boxes, in P/Q/W/E/R/A order
        Assert.Equal(new[] { "P", "Q", "W", "E", "R", "A" }, slots.Select(s => s.Slot).ToArray());
        Assert.Equal(45, Dmg(slots, "P"), 3);
        Assert.Equal(240, Dmg(slots, "Q"), 3);   // 200 + 40
        Assert.Equal(130, Dmg(slots, "W"), 3);
        Assert.Equal(0, Dmg(slots, "E"), 3);     // no E node
        Assert.Equal(300, Dmg(slots, "R"), 3);
        Assert.Equal(70, Dmg(slots, "A"), 3);    // 62 + 8 auto, rune/item excluded
    }

    [Fact]
    public void SkillDamageBySlot_EmptyBreakdown_ReturnsSixZeroBoxes()
    {
        var cr = new ComboResult(0, 0, true, 0, false, new List<NodeBreakdownEntry>(), 0);
        var slots = ComboRunner.SkillDamageBySlot(cr);
        Assert.Equal(6, slots.Count);
        Assert.All(slots, s => Assert.Equal(0, s.Damage, 3));
    }

    private static double Dmg(IReadOnlyList<SkillSlotDamage> slots, string slot)
        => slots.First(s => s.Slot == slot).Damage;
}
