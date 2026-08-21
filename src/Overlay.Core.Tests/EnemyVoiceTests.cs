using Overlay.Core;
using Overlay.Core.Jungle;
using Xunit;

namespace Overlay.Core.Tests;

/// <summary>
/// M31 §B — clip selection and the simple/detail location modes. Pure logic only: the audio
/// device is <c>EnemyVoicePlayer</c>'s problem, which is exactly why these two pieces are split.
/// </summary>
public class EnemyVoiceTests
{
    private static EnemyPresenceAlert Alert(
        EnemyAlertKind kind, string role = "jungle", string zone = "enemy_jungle_top",
        double x = 0.5, double y = 0.5, int group = 1) =>
        new(kind, "Lee Sin", role, zone, "적 정글 상단", x, y, group);

    // ── clip ordering ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Appear_IsRoleThenLocationThenEvent()
    {
        var clips = EnemyVoiceScript.Build(Alert(EnemyAlertKind.Appear), "loc_enemy_camp");
        Assert.Equal(new[] { "role_jungle", "loc_enemy_camp", "event_appear" }, clips);
    }

    [Fact]
    public void Disappear_UsesDisappearEventClip()
    {
        var clips = EnemyVoiceScript.Build(Alert(EnemyAlertKind.Disappear, role: "adc"), "loc_dragon");
        Assert.Equal(new[] { "role_adc", "loc_dragon", "event_disappear" }, clips);
    }

    [Theory]
    [InlineData("top", "role_top")]
    [InlineData("jungle", "role_jungle")]
    [InlineData("mid", "role_mid")]
    [InlineData("adc", "role_adc")]
    [InlineData("support", "role_support")]
    public void EveryRole_MapsToItsClip(string roleKey, string expected)
    {
        var clips = EnemyVoiceScript.Build(Alert(EnemyAlertKind.Disappear, role: roleKey), "loc_baron");
        Assert.Equal(expected, clips[0]);
    }

    [Fact]
    public void UnknownRole_DropsTheRoleClip_ButStillSpeaks()
    {
        // Role resolution can fail (no scoreboard position, no role item). Location + event still
        // carries the useful part, so this must not fall back to silence.
        var clips = EnemyVoiceScript.Build(Alert(EnemyAlertKind.Disappear, role: ""), "loc_river_top");
        Assert.Equal(new[] { "loc_river_top", "event_disappear" }, clips);
    }

    [Fact]
    public void UnresolvedLocation_StillAnnouncesWhoAndWhat()
    {
        var clips = EnemyVoiceScript.Build(Alert(EnemyAlertKind.Disappear, role: "mid"), null);
        Assert.Equal(new[] { "role_mid", "event_disappear" }, clips);
    }

    [Fact]
    public void NeitherRoleNorLocation_IsSilent()
    {
        // A bare "발견" tells the user nothing, so producing no clips is the honest outcome.
        Assert.Empty(EnemyVoiceScript.Build(Alert(EnemyAlertKind.Appear, role: ""), null));
    }

    // ── group alerts ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Group_IsCountThenEvent_WithNoLocation(int count)
    {
        // The group scattered, so naming one point would misreport where they went.
        var clips = EnemyVoiceScript.Build(
            Alert(EnemyAlertKind.GroupDisappear, role: "", group: count), "loc_enemy_camp");
        Assert.Equal(new[] { $"group_{count}", "event_disappear" }, clips);
    }

    [Theory]
    [InlineData(1, "group_2")]
    [InlineData(9, "group_5")]
    public void Group_CountOutsideTwoToFive_ClampsToAnExistingClip(int count, string expected)
    {
        // Only group_2..group_5 exist; an out-of-range count must not name a missing file.
        var clips = EnemyVoiceScript.Build(
            Alert(EnemyAlertKind.GroupDisappear, group: count), null);
        Assert.Equal(expected, clips[0]);
    }

    // ── location modes ───────────────────────────────────────────────────────────────────

    private static VoiceLocationResolver Resolver() =>
        VoiceLocationResolver.FromData(
            new Dictionary<string, string>
            {
                ["enemy_jungle_top"] = "loc_enemy_camp",
                ["top_lane"] = "loc_top_lane",
            },
            new[]
            {
                new VoiceLocationResolver.Camp("enemy_red", "loc_enemy_red", 0.48, 0.26),
                new VoiceLocationResolver.Camp("baron", "loc_baron", 0.34, 0.30),
                new VoiceLocationResolver.Camp("dragon", "loc_dragon", 0.66, 0.71),
            },
            new[] { "top_lane" });

    [Fact]
    public void Simple_MapsZoneKeyToBroadLocation()
    {
        Assert.Equal("loc_enemy_camp", Resolver().ResolveSimple("enemy_jungle_top"));
    }

    [Fact]
    public void Simple_UnmappedZone_ResolvesToNothing()
    {
        Assert.Null(Resolver().ResolveSimple("no_such_zone"));
        Assert.Null(Resolver().ResolveSimple(""));
    }

    [Fact]
    public void Detail_PicksTheNearestCamp()
    {
        // Sitting almost exactly on Dragon.
        Assert.Equal("loc_dragon", Resolver().ResolveDetail(0.65, 0.70));
        // Nearer Baron than enemy red.
        Assert.Equal("loc_baron", Resolver().ResolveDetail(0.35, 0.32));
    }

    [Fact]
    public void Detail_AndSimple_CanDisagree_WhichIsThePoint()
    {
        // Same sighting: simple names the broad zone, detail names the camp. Detail mode exists
        // precisely because "적 캠프" is less actionable than "적 레드".
        var r = Resolver();
        Assert.Equal("loc_enemy_camp", r.Resolve("enemy_jungle_top", 0.47, 0.27, "simple"));
        Assert.Equal("loc_enemy_red", r.Resolve("enemy_jungle_top", 0.47, 0.27, "detail"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SIMPLE")]
    [InlineData("nonsense")]
    public void Resolve_TreatsAnythingButDetailAsSimple(string? mode)
    {
        // The mode arrives as a raw config string, so a hand-edited value must degrade, not throw.
        Assert.Equal("loc_enemy_camp", Resolver().Resolve("enemy_jungle_top", 0.47, 0.27, mode));
    }

    [Fact]
    public void Detail_KeepsLaneZones_InsteadOfNamingSomeUnrelatedCamp()
    {
        // A bot-lane sighting's nearest camp is Dragon, so plain nearest-camp would announce
        // "드래곤" for someone standing in lane — less accurate than the zone name it replaced.
        // Lanes are listed in detailModeKeepsZone precisely to prevent that.
        var r = Resolver();
        Assert.Equal("loc_top_lane", r.Resolve("top_lane", 0.34, 0.31, "detail"));
        // ...while a jungle zone still gets the finer camp name.
        Assert.Equal("loc_baron", r.Resolve("enemy_jungle_top", 0.34, 0.31, "detail"));
    }

    [Fact]
    public void ShippedData_KeepsAllThreeLanesCoarseInDetailMode()
    {
        var r = VoiceLocationResolver.TryLoad();
        Assert.NotNull(r);
        // Coordinates deep in each lane; detail mode must still name the lane.
        Assert.Equal("loc_bot_lane", r!.Resolve("bot_lane", 0.80, 0.90, "detail"));
        Assert.Equal("loc_top_lane", r.Resolve("top_lane", 0.20, 0.10, "detail"));
        Assert.Equal("loc_mid_lane", r.Resolve("mid_lane", 0.50, 0.50, "detail"));
    }

    [Fact]
    public void Resolve_DetailFallsBackToSimple_WhenNoCampsAreNear()
    {
        // A resolver with no camps at all still answers via the zone table rather than going quiet.
        var r = VoiceLocationResolver.FromData(
            new Dictionary<string, string> { ["top_lane"] = "loc_top_lane" },
            Array.Empty<VoiceLocationResolver.Camp>());
        Assert.Equal("loc_top_lane", r.Resolve("top_lane", 0.1, 0.1, "detail"));
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNullInsteadOfThrowing()
    {
        // Missing voice data must degrade to "no location callout", never crash the alert path.
        Assert.Null(VoiceLocationResolver.TryLoad(Path.Combine(Path.GetTempPath(), "no_such_camps.json")));
    }

    // ── shipped data ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ShippedCampData_LoadsAndResolvesEveryZone()
    {
        var r = VoiceLocationResolver.TryLoad();
        Assert.NotNull(r);
        Assert.Equal(16, r!.Camps.Count);

        // Every zone the tracker can emit must map to a clip, or that sighting goes unnamed.
        foreach (var zone in new[]
                 {
                     "top_lane", "mid_lane", "bot_lane", "river_top", "river_bottom",
                     "baron_pit", "dragon_pit", "our_jungle_top", "our_jungle_bottom",
                     "enemy_jungle_top", "enemy_jungle_bottom",
                 })
            Assert.False(string.IsNullOrEmpty(r.ResolveSimple(zone)), $"zone '{zone}' has no clip");
    }

    // ── §43-F lane corridors: a lane position must be named after the LANE ──────────────

    /// <summary>
    /// (2026-07-20, live) A bot-lane sighting was announced as "두꺼비". Detail mode searched CAMPS
    /// only, so a lane could never win — the nearest anchor to a bot-lane point is a jungle camp.
    /// Lanes are now candidates in the same nearest-anchor search.
    /// </summary>
    [Theory]
    [InlineData(0.55, 0.905, "loc_bot_lane")]   // bottom edge, mid-corridor
    [InlineData(0.90, 0.55, "loc_bot_lane")]    // right edge, enemy half of bot
    [InlineData(0.095, 0.45, "loc_top_lane")]   // left edge
    [InlineData(0.45, 0.10, "loc_top_lane")]    // top edge
    [InlineData(0.50, 0.50, "loc_mid_lane")]    // dead centre = mid
    public void DetailMode_OnALane_NamesTheLane(double x, double y, string expected)
    {
        var r = VoiceLocationResolver.TryLoad();
        Assert.NotNull(r);
        Assert.Equal(expected, r!.ResolveDetail(x, y));
    }

    /// <summary>Camps must still win where they actually are — adding lanes must not swamp them.</summary>
    [Fact]
    public void DetailMode_AtACamp_StillNamesTheCamp()
    {
        var r = VoiceLocationResolver.TryLoad();
        Assert.NotNull(r);
        foreach (var camp in r!.Camps)
            Assert.Equal(camp.Voice, r.ResolveDetail(camp.X01, camp.Y01));
    }
}
