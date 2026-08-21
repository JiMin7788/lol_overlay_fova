using Overlay.Core.Lcu;
using Overlay.Core.Runes;

namespace Overlay.Core.Tests;

/// <summary>
/// The dashboard rune editor's selection rules (RunePageEditor) against the REAL bundled
/// CommunityDragon catalog (perkstyles.json / perks.json) — same real-data posture as the
/// skill-damage tests. Style/perk ids used here are read FROM the catalog, not hardcoded, so a
/// patch that reshuffles perks does not silently rot the assertions.
/// </summary>
public class RunePageEditorTests
{
    public RunePageEditorTests() => RuneCatalog.ResetForTests();

    private static RuneStyleInfo Style(int index) => RuneCatalog.Styles[index];

    /// <summary>A complete page built from the first entry of every relevant row of two catalog
    /// styles.</summary>
    private static RunePage CompletePage(RuneStyleInfo primary, RuneStyleInfo sub) => new()
    {
        PrimaryStyleId = primary.Id,
        SubStyleId = sub.Id,
        PerkIds = new List<int>
        {
            primary.PerkRows[0][0], primary.PerkRows[1][0], primary.PerkRows[2][0], primary.PerkRows[3][0],
            sub.PerkRows[1][0], sub.PerkRows[2][0],
            primary.StatRows[0][0], primary.StatRows[1][0], primary.StatRows[2][0],
        },
    };

    [Fact]
    public void Catalog_Loads_FiveStyles_WithKeystoneMinorAndShardRows()
    {
        Assert.Equal(5, RuneCatalog.Styles.Count);
        foreach (var s in RuneCatalog.Styles)
        {
            Assert.Equal(4, s.PerkRows.Count);   // keystone + 3 minors
            Assert.Equal(3, s.StatRows.Count);   // shard rows come from data, never hardcoded
            Assert.NotEmpty(s.PerkRows[0]);
            Assert.NotEmpty(s.IconPath);
            // Every perk in the tree resolves to display data with a normalized icon path.
            foreach (var row in s.PerkRows)
                foreach (int id in row)
                {
                    var perk = RuneCatalog.GetPerk(id);
                    Assert.NotNull(perk);
                    Assert.StartsWith("perk-images/", perk!.Value.IconPath);
                }
        }
    }

    [Fact]
    public void FromPage_RoundTrips_ACompletePage()
    {
        var page = CompletePage(Style(0), Style(1));
        var ed = RunePageEditor.FromPage(page);

        Assert.True(ed.IsComplete);
        var back = ed.ToPage();
        Assert.NotNull(back);
        Assert.Equal(page.PrimaryStyleId, back!.PrimaryStyleId);
        Assert.Equal(page.SubStyleId, back.SubStyleId);
        Assert.Equal(page.PerkIds, back.PerkIds);
    }

    [Fact]
    public void ToPage_Null_WhileIncomplete_SoLiveApplyNeverSendsHalfPages()
    {
        var ed = RunePageEditor.FromPage(new RunePage
        {
            PrimaryStyleId = Style(0).Id,
            SubStyleId = Style(1).Id,
            PerkIds = new List<int> { Style(0).PerkRows[0][0] }, // keystone only
        });
        Assert.False(ed.IsComplete);
        Assert.Null(ed.ToPage());
    }

    [Fact]
    public void SelectPrimaryPerk_ReplacesThatRowOnly()
    {
        var p = Style(0);
        var ed = RunePageEditor.FromPage(CompletePage(p, Style(1)));

        int replacement = p.PerkRows[2][1];
        Assert.True(ed.SelectPrimaryPerk(2, replacement));

        Assert.Equal(replacement, ed.PrimaryPick(2));
        Assert.Equal(p.PerkRows[1][0], ed.PrimaryPick(1)); // untouched
        Assert.True(ed.IsComplete);
    }

    [Fact]
    public void SelectPrimaryPerk_RejectsAPerkFromAnotherRowOrStyle()
    {
        var p = Style(0);
        var ed = RunePageEditor.FromPage(CompletePage(p, Style(1)));

        Assert.False(ed.SelectPrimaryPerk(1, p.PerkRows[2][0]));          // wrong row
        Assert.False(ed.SelectPrimaryPerk(1, Style(2).PerkRows[1][0]));   // wrong style
        Assert.Equal(p.PerkRows[1][0], ed.PrimaryPick(1));
    }

    [Fact]
    public void SubPicks_SameRowReplaces_ThirdRowEvictsOldest()
    {
        var sub = Style(1);
        var ed = RunePageEditor.FromPage(CompletePage(Style(0), sub));
        // Page has sub picks in rows 1 and 2 (oldest = row 1).

        // Same-row click replaces in place.
        int row2Alt = sub.PerkRows[2][1];
        Assert.True(ed.SelectSubPerk(2, row2Alt));
        Assert.Equal(row2Alt, ed.SubPick(2));
        Assert.Equal(sub.PerkRows[1][0], ed.SubPick(1));

        // A third distinct row evicts the OLDEST pick (row 1).
        Assert.True(ed.SelectSubPerk(3, sub.PerkRows[3][0]));
        Assert.Null(ed.SubPick(1));
        Assert.Equal(row2Alt, ed.SubPick(2));
        Assert.Equal(sub.PerkRows[3][0], ed.SubPick(3));
        Assert.True(ed.IsComplete);
    }

    [Fact]
    public void SelectSubStyle_ClearsSubPicks_AndRejectsThePrimary()
    {
        var ed = RunePageEditor.FromPage(CompletePage(Style(0), Style(1)));

        ed.SelectSubStyle(Style(0).Id);              // == primary -> rejected
        Assert.Equal(Style(1).Id, ed.SubStyleId);

        ed.SelectSubStyle(Style(2).Id);
        Assert.Equal(Style(2).Id, ed.SubStyleId);
        Assert.Null(ed.SubPick(1));
        Assert.Null(ed.SubPick(2));
        Assert.False(ed.IsComplete);                 // sub picks must be rebuilt
    }

    [Fact]
    public void SelectPrimaryStyle_EqualToSecondary_SwapsStyles()
    {
        var ed = RunePageEditor.FromPage(CompletePage(Style(0), Style(1)));

        ed.SelectPrimaryStyle(Style(1).Id);

        Assert.Equal(Style(1).Id, ed.PrimaryStyleId);
        Assert.Equal(Style(0).Id, ed.SubStyleId);
        Assert.Null(ed.PrimaryPick(0));              // picks cleared on a style change
        Assert.Null(ed.SubPick(1));
    }

    [Fact]
    public void Shards_AreRowAddressed_BecauseIdsRepeatAcrossRows()
    {
        var p = Style(0);
        var ed = RunePageEditor.FromPage(CompletePage(p, Style(1)));

        // Find an id that appears in more than one shard row (e.g. Adaptive Force) — the reason
        // shard clicks carry the row.
        int? shared = p.StatRows[0].Intersect(p.StatRows[1]).Cast<int?>().FirstOrDefault();
        if (shared is int id)
        {
            Assert.True(ed.SelectShard(1, id));
            Assert.Equal(id, ed.Shard(1));
            Assert.Equal(p.StatRows[0][0], ed.Shard(0)); // row 0 untouched
        }

        Assert.False(ed.SelectShard(0, -1));
        // Shards survive a style change (they are style-independent).
        ed.SelectPrimaryStyle(Style(2).Id);
        Assert.NotNull(ed.Shard(0));
    }
}
